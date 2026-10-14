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
        catch (HttpRequestException e)
        {
            logger.LogWarning("Codex upstream request failed: {error}", e.Message);
            return UpstreamResult.NotServed(new ModelFault(FaultKind.Unreachable));
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
            var errorType = call.Request["stream"] is JsonValue s && s.TryGetValue<bool>(out var streaming) && streaming
                ? await StreamAsync(context, translator, response, ct)
                : await AggregateAsync(context, translator, response, ct);
            if (errorType == AggregateFailover)
            {
                return UpstreamResult.NotServed(new ModelFault(FaultKind.Limited, limits.ResetAt));
            }
            return UpstreamResult.ServedWith(errorType switch
            {
                "rate_limit_error" => new ModelFault(FaultKind.Limited, limits.ResetAt),
                "overloaded_error" => new ModelFault(FaultKind.Overloaded),
                _ when limits.Exhausted => new ModelFault(FaultKind.Limited, limits.ResetAt),
                _ => null,
            });
        }
    }

    // Returned by AggregateAsync when a usage-limit failure arrived before anything was written.
    private const string AggregateFailover = "failover";

    /// <summary>Sends the translated request with the access token used; a null response means an error was already written.</summary>
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
        catch (CodexOAuthException e)
        {
            logger.LogWarning("Codex access token refresh failed with status {status}", e.Status);
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", e.Message);
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

    /// <returns>The Anthropic error type the stream ended with, or null when it completed.</returns>
    private async Task<string?> StreamAsync(HttpContext context, ResponsesStreamTranslator translator, HttpResponseMessage response, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        string? errorType = null;
        await WriteAsync(context, [translator.Start()], ct);
        await TranslateAsync(translator, response, events =>
        {
            errorType ??= events.Where(e => Str(e["type"]) == "error").Select(e => Str(e["error"]?["type"])).FirstOrDefault();
            return WriteAsync(context, events, ct);
        }, ct);
        return errorType;
    }

    /// <returns><see cref="AggregateFailover"/> when nothing was written because of a usage limit, else as <see cref="StreamAsync"/>.</returns>
    private async Task<string?> AggregateAsync(HttpContext context, ResponsesStreamTranslator translator, HttpResponseMessage response, CancellationToken ct)
    {
        var builder = new AnthropicMessageBuilder();
        builder.Apply(translator.Start());
        await TranslateAsync(translator, response, events =>
        {
            events.ForEach(builder.Apply);
            return Task.CompletedTask;
        }, ct);

        if (builder.Error is { } error)
        {
            var type = ChatRequestTranslator.Str(error["type"]) ?? "api_error";
            if (type == "rate_limit_error")
            {
                return AggregateFailover;
            }
            await AnthropicError.WriteAsync(context, CodexErrors.Status(type), type, ChatRequestTranslator.Str(error["message"]) ?? "Codex response failed.");
            return type;
        }
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(builder.Message, WireJson.Relaxed), ct);
        return null;
    }

    /// <summary>Feeds every upstream event to the translator until it ends; a stalled, broken or truncated stream ends with an error event.</summary>
    private async Task TranslateAsync(ResponsesStreamTranslator translator, HttpResponseMessage response, Func<List<JsonObject>, Task> emit, CancellationToken ct)
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
                        line = await reader.ReadLineAsync(idle.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
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
                    await emit(Parse(payload) is { } evt ? translator.Feed(evt) : translator.Abort("Codex sent a malformed stream event."));
                }
                if (line is null && !translator.Ended)
                {
                    await emit(translator.Abort("Codex stream ended before completion."));
                }
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Codex stream failed: {error}", e.Message);
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

    private static async Task WriteAsync(HttpContext context, List<JsonObject> events, CancellationToken ct)
    {
        if (events.Count == 0)
        {
            return;
        }
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(ResponsesStreamTranslator.Format(events)), ct);
        await context.Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// A limit, plan rejection or overload is returned unwritten for failover. Anything else keeps the upstream status
    /// with an Anthropic-shaped body.
    /// </summary>
    private async Task<UpstreamResult> ErrorAsync(HttpContext context, HttpResponseMessage response, LimitReading limits, DateTimeOffset now, CancellationToken ct)
    {
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
            logger.LogWarning("Codex answered HTTP {status} ({code}); not serving this request from it", status, code);
            return UpstreamResult.NotServed(fault);
        }
        var type = CodexErrors.TypeForStatus(status);
        if (status is 401 or 403)
        {
            // A plan or usage refusal is the user's to know about; a rejected credential is the router's to fix, and
            // passed through it would make the client ask for its own login.
            type = CodexErrors.Type(code);
            status = CodexErrors.Status(type);
            if (type == "api_error")
            {
                message = $"Codex rejected the subscription credentials (HTTP {(int)response.StatusCode}); run 'simple-router login codex'.";
            }
        }
        await AnthropicError.WriteAsync(context, status, type, message ?? $"Codex returned HTTP {(int)response.StatusCode}.");
        return UpstreamResult.Served;
    }

    /// <summary>
    /// Codex refusals another model may do better on: usage limits (429, <c>usage_limit_reached</c>), a plan without Codex
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
        if (status == 429 || CodexErrors.Type(code) == "rate_limit_error")
        {
            return new ModelFault(FaultKind.Limited, reset);
        }
        return status is 500 or 502 or 503 or 504 or 529 ? new ModelFault(FaultKind.Overloaded, reset) : null;
    }
}
