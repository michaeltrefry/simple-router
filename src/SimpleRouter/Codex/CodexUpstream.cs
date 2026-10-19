using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using SimpleRouter.Api;
using SimpleRouter.Local;
using SimpleRouter.Routing;
using SimpleRouter.Usage;

namespace SimpleRouter.Codex;

/// <summary>
/// Serves Messages API calls from the Codex (ChatGPT) subscription's Responses backend. The backend only streams, so a
/// non-streaming client gets the stream folded into one message. A limit, plan rejection, overload or connection failure
/// that arrives before anything is written is returned unwritten so the router can fail over.
/// </summary>
public sealed class CodexUpstream(
    IHttpClientFactory httpClientFactory,
    CodexTokenProvider tokens,
    IOptions<RouterOptions> options,
    TimeProvider time,
    ILogger<CodexUpstream> logger) : IModelUpstream
{
    public const string HttpClientName = "codex-upstream";
    private const int MaxErrorBodyChars = 64 * 1024;

    // The Codex backend only serves clients that identify as the Codex CLI.
    internal const string Originator = "codex_cli_rs";

    public ProviderKind Provider => ProviderKind.Codex;

    public async Task<UpstreamResult> SendAsync(UpstreamCall call)
    {
        var context = call.Context;
        var ct = context.RequestAborted;
        if (call.CountTokens)
        {
            await context.Response.WriteAsJsonAsync(new { input_tokens = call.EstimatedInputTokens }, ct);
            return UpstreamResult.Served;
        }

        var cacheKey = new Guid(Convert.FromHexString(call.Session.Key[..32])).ToString();
        var body = JsonSerializer.SerializeToUtf8Bytes(ResponsesRequestTranslator.Translate(call.Request, call.Model, cacheKey), WireJson.Relaxed);
        HttpResponseMessage? response;
        try
        {
            string? accessToken;
            (response, accessToken) = await SendOnceAsync(context, body, cacheKey, rejected: null);
            if (response?.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The access token may have been revoked before its expiry; one forced refresh decides.
                response.Dispose();
                (response, _) = await SendOnceAsync(context, body, cacheKey, rejected: accessToken);
            }
        }
        catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            RequestDiagnostics.Fail(context, e is OperationCanceledException ? "upstream_timeout" : "upstream_unreachable");
            logger.LogWarning("Codex upstream request failed");
            return UpstreamResult.NotServed(new ModelFault(FaultKind.Unreachable));
        }
        catch (CodexOAuthException e)
        {
            logger.LogWarning("Codex access token refresh failed with status {status}", e.Status);
            return UpstreamResult.NotServed(ModelFault.RefreshFailed(e.Status));
        }
        if (response is null)
        {
            return UpstreamResult.Served;
        }

        using (response)
        {
            var now = time.GetUtcNow();
            var limits = LimitHeaders.Codex(response.Headers, now);
            var translator = new ResponsesStreamTranslator(call.Model.Id, call.EstimatedInputTokens);
            if (!response.IsSuccessStatusCode)
            {
                return await ErrorAsync(context, response, limits, now, ct);
            }
            var written = call.Request["stream"] is JsonValue s && s.TryGetValue<bool>(out var streaming) && streaming
                ? await StreamAsync(context, translator, response, ct)
                : await AggregateAsync(context, translator, response, ct);
            var fault = StreamFault(translator, limits);
            if (!written)
            {
                return UpstreamResult.NotServed(fault!);
            }
            return UpstreamResult.ServedWith(fault ?? (limits.Exhausted ? new ModelFault(FaultKind.Limited, limits.ResetAt) : null));
        }
    }

    /// <summary>A stream failure another model may do better on: a limit, an overload or a prompt too long for the context.</summary>
    private static ModelFault? StreamFault(ResponsesStreamTranslator translator, LimitReading limits) => translator switch
    {
        { ErrorCode: "context_length_exceeded" } => new ModelFault(FaultKind.ContextOverflow, Message: translator.ErrorMessage),
        { ErrorType: "rate_limit_error" } => new ModelFault(FaultKind.Limited, limits.ResetAt),
        { ErrorType: "overloaded_error" } => new ModelFault(FaultKind.Overloaded),
        _ => null,
    };

    private static bool CanFailOver(ResponsesStreamTranslator translator) => StreamFault(translator, LimitReading.Open) is not null;

    /// <summary>
    /// Sends the translated request with the access token used; a null response means an error was already written. A
    /// failed token refresh throws <see cref="CodexOAuthException"/>.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, string? AccessToken)> SendOnceAsync(HttpContext context, byte[] body, string cacheKey, string? rejected)
    {
        var ct = context.RequestAborted;
        CodexAccess access;
        try
        {
            access = await tokens.GetAccessAsync(ct, rejected);
        }
        catch (CodexNotLoggedInException e)
        {
            await AnthropicError.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "api_error", e.Message);
            return (null, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.Codex.ApiBaseUrl.TrimEnd('/') + "/responses")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", access.AccountId);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.TryAddWithoutValidation("originator", Originator);
        request.Headers.TryAddWithoutValidation("User-Agent", Originator);
        request.Headers.TryAddWithoutValidation("session_id", cacheKey);
        request.Headers.Accept.ParseAdd("text/event-stream");

        return (await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct), access.AccessToken);
    }

    /// <summary>
    /// Streams the translation, withholding <c>message_start</c> until the first content or terminal event (or the end of
    /// the <see cref="StreamStartGrace"/>) so a failure another model may serve (<see cref="StreamFault"/>) that arrives
    /// first leaves the response untouched.
    /// </summary>
    /// <returns>False when nothing was written.</returns>
    private async Task<bool> StreamAsync(HttpContext context, ResponsesStreamTranslator translator, HttpResponseMessage response, CancellationToken ct)
    {
        var start = translator.Start();
        var started = false;
        void Begin(List<JsonObject> events)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            events.Insert(0, start);
            started = true;
        }
        await using var output = new StreamKeepAlive(context.Response.Body, options.Value.Limits.KeepAliveInterval, time, ct);
        var grace = new StreamStartGrace(options.Value.Limits.StreamStartGrace, time, async () =>
        {
            if (!started)
            {
                var events = new List<JsonObject>();
                Begin(events);
                await WriteAsync(output, events, translator.Ended);
            }
        });
        await TranslateAsync(context, translator, response, async events =>
        {
            if (!started)
            {
                if (events.Count == 0 || (CanFailOver(translator) && events.All(e => Str(e["type"]) == "error")))
                {
                    return;
                }
                Begin(events);
            }
            await WriteAsync(output, events, translator.Ended);
        }, ct, grace);
        return started;
    }

    /// <returns>False when a failure another model may serve left nothing to send.</returns>
    private async Task<bool> AggregateAsync(HttpContext context, ResponsesStreamTranslator translator, HttpResponseMessage response, CancellationToken ct)
    {
        var builder = new AnthropicMessageBuilder();
        builder.Apply(translator.Start());
        await TranslateAsync(context, translator, response, events =>
        {
            events.ForEach(builder.Apply);
            return Task.CompletedTask;
        }, ct, grace: null);

        if (builder.Error is { } error)
        {
            if (CanFailOver(translator))
            {
                return false;
            }
            var type = ChatRequestTranslator.Str(error["type"]) ?? "api_error";
            await AnthropicError.WriteAsync(context, CodexErrors.Status(type), type, ChatRequestTranslator.Str(error["message"]) ?? "Codex response failed.");
            return true;
        }
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(builder.Message, WireJson.Relaxed), ct);
        return true;
    }

    /// <summary>Feeds every upstream event to the translator until it ends; a stalled, broken or truncated stream ends with an error event.</summary>
    private async Task TranslateAsync(HttpContext context, ResponsesStreamTranslator translator, HttpResponseMessage response, Func<List<JsonObject>, Task> emit,
        CancellationToken ct, StreamStartGrace? grace)
    {
        try
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
            var idleTimeout = options.Value.Codex.StreamIdleTimeout;
            var data = new List<string>();
            while (!translator.Ended)
            {
                string? line;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(idleTimeout);
                    try
                    {
                        var read = reader.ReadLineAsync(idle.Token).AsTask();
                        line = grace is null ? await read : await grace.AwaitAsync(read, ct);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        RequestDiagnostics.Fail(context, "upstream_timeout");
                        await emit(translator.Abort($"Codex sent nothing for {idleTimeout.TotalSeconds:0.#} s."));
                        return;
                    }
                }

                if (line is { Length: > 0 })
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        var value = line["data:".Length..];
                        data.Add(value.StartsWith(' ') ? value[1..] : value);
                    }
                    continue;
                }
                // A blank line, or the end of the body, dispatches the pending event.
                if (data.Count > 0)
                {
                    var payload = string.Join("\n", data).Trim();
                    data.Clear();
                    var evt = Parse(payload);
                    if (evt is not null && (Str(evt["type"]) is null
                        || (Str(evt["type"]) is "response.completed" or "response.incomplete" && evt["response"] is not JsonObject)))
                        evt = null;
                    if (evt is null) RequestDiagnostics.Fail(context, "malformed_response");
                    List<JsonObject> events;
                    try
                    {
                        events = evt is not null ? translator.Feed(evt) : translator.Abort("Codex sent a malformed stream event.");
                    }
                    catch (InvalidOperationException)
                    {
                        RequestDiagnostics.Fail(context, "malformed_response");
                        events = translator.Abort("Codex sent an invalid stream event.");
                    }
                    if (events.Any(e => Str(e["type"]) == "error") && RequestDiagnostics.Get(context) is null)
                        RequestDiagnostics.Fail(context, "upstream_error");
                    await emit(events);
                }
                if (line is null && !translator.Ended)
                {
                    RequestDiagnostics.Fail(context, "truncated_stream");
                    await emit(translator.Abort("Codex stream ended before completion."));
                }
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException && !ct.IsCancellationRequested)
        {
            RequestDiagnostics.Fail(context, "transport_error");
            logger.LogWarning("Codex stream failed");
            await emit(translator.Abort("Codex stream was interrupted."));
        }
    }

    private static string? Str(JsonNode? node) => ChatRequestTranslator.Str(node);

    private static JsonNode? Field(JsonNode? node, string name) => node is JsonObject o ? o[name] : null;

    private static JsonObject? Parse(string payload)
    {
        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(StreamKeepAlive output, List<JsonObject> events, bool final)
    {
        if (events.Count == 0)
        {
            return;
        }
        await output.WriteAsync(Encoding.UTF8.GetBytes(ResponsesStreamTranslator.Format(events)), final);
    }

    /// <summary>
    /// A limit, plan rejection, overload or a credential still rejected after the forced refresh is returned unwritten for
    /// failover. Anything else keeps the upstream status with an Anthropic-shaped body.
    /// </summary>
    private async Task<UpstreamResult> ErrorAsync(HttpContext context, HttpResponseMessage response, LimitReading limits, DateTimeOffset now, CancellationToken ct)
    {
        RequestDiagnostics.Fail(context, "http_error");
        var status = (int)response.StatusCode;
        // Read at most one character past the limit, so an oversized or endless body is never buffered whole.
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        var buffer = new char[MaxErrorBodyChars + 1];
        var length = await reader.ReadBlockAsync(buffer, ct);
        string? message = null;
        string? code = null;
        DateTimeOffset? bodyReset = null;
        try
        {
            var error = JsonNode.Parse(length > MaxErrorBodyChars ? "" : new string(buffer, 0, length));
            var nested = Field(error, "error") ?? Field(error, "detail");
            message = Str(Field(nested, "message")) ?? Str(Field(error, "detail"));
            code = Str(Field(nested, "code")) ?? Str(Field(nested, "type"));
            bodyReset = LimitHeaders.Instant(Field(nested, "resets_at")?.ToString())
                ?? LimitHeaders.After(now, double.TryParse(Field(nested, "resets_in_seconds")?.ToString(), CultureInfo.InvariantCulture, out var inSeconds) ? inSeconds : null);
        }
        catch (JsonException)
        {
        }
        if (Fault(status, code, message, (limits.Exhausted ? limits.ResetAt : null) ?? bodyReset ?? LimitHeaders.RetryAfter(response.Headers, now)) is { } fault)
        {
            logger.LogWarning("Codex answered HTTP {status}; not serving this request from it", status);
            return UpstreamResult.NotServed(fault);
        }
        var type = CodexErrors.TypeForStatus(status);
        if (status is 401 or 403)
        {
            // A rejected credential is the router's to fix: passed through it would make the client ask for its own login.
            type = CodexErrors.Type(code);
            if (type == "api_error")
            {
                logger.LogWarning("Codex rejected the subscription credentials (HTTP {status}); not serving this request from it", status);
                return UpstreamResult.NotServed(new ModelFault(FaultKind.AuthRejected));
            }
            status = CodexErrors.Status(type);
        }
        await AnthropicError.WriteAsync(context, status, type, message ?? $"Codex returned HTTP {(int)response.StatusCode}.");
        return UpstreamResult.Served;
    }

    /// <summary>
    /// Codex refusals another model may do better on: a prompt too long for the context, usage limits (429, <c>usage_limit_reached</c>), a plan without Codex
    /// or this model (<c>usage_not_included</c>, model-not-available codes or Codex's "not supported when using Codex
    /// with a ChatGPT account"), and overload or server failure.
    /// </summary>
    private static ModelFault? Fault(int status, string? code, string? message, DateTimeOffset? reset)
    {
        if (code is "usage_not_included" or "model_not_found" or "unsupported_model" or "model_not_available"
            || (status is 400 or 404 && message?.Contains("not supported when using Codex", StringComparison.OrdinalIgnoreCase) == true))
        {
            return new ModelFault(FaultKind.PlanRejected);
        }
        if (code == "context_length_exceeded" || (status is 400 or 413 && ContextOverflow.IsReported(message)))
        {
            return new ModelFault(FaultKind.ContextOverflow, Message: message);
        }
        if (status == 429 || CodexErrors.Type(code) == "rate_limit_error")
        {
            return new ModelFault(FaultKind.Limited, reset);
        }
        return status is 500 or 502 or 503 or 504 or 529 ? new ModelFault(FaultKind.Overloaded, reset) : null;
    }
}
