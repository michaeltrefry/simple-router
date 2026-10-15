using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using SimpleRouter.Api;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.Local;

/// <summary>
/// Serves Messages API calls from a local OpenAI-compatible Chat Completions endpoint, translating both ways. A refused
/// connection (which also marks the endpoint unhealthy), a 429 or 5xx, an error reported before any streamed output, or a
/// context-length rejection is returned unwritten so the router can fail over.
/// </summary>
public sealed class LocalUpstream(
    IHttpClientFactory httpClientFactory,
    SecretProtector secrets,
    LocalHealth health,
    Microsoft.Extensions.Options.IOptions<RouterOptions> options,
    TimeProvider time,
    ILogger<LocalUpstream> logger) : IModelUpstream
{
    public const string HttpClientName = "local-upstream";
    private const int MaxErrorBodyChars = 64 * 1024;

    public ProviderKind Provider => ProviderKind.Local;

    public static string Endpoint(CatalogModel model, string path) =>
        (model.BaseUrl ?? throw new InvalidOperationException($"Local model {model.Id} has no base URL.")).TrimEnd('/') + "/" + path;

    public async Task<UpstreamResult> SendAsync(UpstreamCall call)
    {
        var (context, model) = (call.Context, call.Model);
        var ct = context.RequestAborted;
        if (call.CountTokens)
        {
            await context.Response.WriteAsJsonAsync(new { input_tokens = call.EstimatedInputTokens }, ct);
            return UpstreamResult.Served;
        }

        var chat = ChatRequestTranslator.Translate(call.Request, model, call.EstimatedInputTokens);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(model, "chat/completions"))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(chat, WireJson.Relaxed)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (model.ProtectedApiKey is { } key)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Unprotect(key));
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning("Local model {model} is unreachable: {error}", model.Id, e.Message);
            health.MarkUnhealthy(model.Id);
            return UpstreamResult.NotServed(new ModelFault(FaultKind.Unreachable));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return await ErrorAsync(context, model, response, ct);
            }
            if (chat["stream"] is not null)
            {
                if (await StreamAsync(context, model, call.EstimatedInputTokens, response, ct) is { } fault)
                {
                    logger.LogWarning("Local model {model} failed before any output; not serving this request from it ({reason})", model.Id, fault.Kind);
                    return UpstreamResult.NotServed(fault);
                }
            }
            else
            {
                await WriteMessageAsync(context, model, call.EstimatedInputTokens, response, ct);
            }
            return UpstreamResult.Served;
        }
    }

    private static async Task WriteMessageAsync(HttpContext context, CatalogModel model, int estimate, HttpResponseMessage response, CancellationToken ct)
    {
        JsonObject? chat;
        try
        {
            chat = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct) as JsonObject;
        }
        catch (JsonException)
        {
            chat = null;
        }
        if (chat is null)
        {
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", $"Local model {model.Id} returned an unreadable response.");
            return;
        }
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(ChatResponseTranslator.Translate(chat, model.Id, estimate), WireJson.Relaxed), ct);
    }

    /// <summary>
    /// Streams the translation, withholding <c>message_start</c> until the first output or closing event (or the end of the
    /// <see cref="StreamStartGrace"/>), so an error the endpoint reports before any output leaves the response untouched for
    /// another model, or is answered as the client's request error.
    /// </summary>
    /// <returns>Null once written; else why nothing was.</returns>
    private async Task<ModelFault?> StreamAsync(HttpContext context, CatalogModel model, int estimate, HttpResponseMessage response, CancellationToken ct)
    {
        var translator = new ChatStreamTranslator(model.Id, estimate);
        var start = translator.Start();
        var started = false;
        string Begin(string sse)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            started = true;
            return start + sse;
        }
        await using var output = new StreamKeepAlive(context.Response.Body, options.Value.Limits.KeepAliveInterval, time, ct);
        async Task Write(string sse, bool final)
        {
            if (sse.Length == 0)
            {
                return;
            }
            await output.WriteAsync(Encoding.UTF8.GetBytes(started ? sse : Begin(sse)), final);
        }
        var grace = new StreamStartGrace(options.Value.Limits.StreamStartGrace, time, async () =>
        {
            if (!started)
            {
                await Write(Begin(""), final: false);
            }
        });

        string tail;
        try
        {
            var (closing, early, rejection) = await TranslateChunksAsync(translator, sse => Write(sse, final: false), () => started, grace, response, ct);
            if (early is not null)
            {
                return early;
            }
            if (rejection is not null)
            {
                await AnthropicError.WriteAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error", rejection);
                return null;
            }
            tail = closing!;
        }
        catch (Exception e) when (e is IOException or HttpRequestException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Local model {model} stream failed: {error}", model.Id, e.Message);
            tail = translator.Abort($"Local model {model.Id} stream was interrupted.");
        }
        await Write(tail, final: true);
        return null;
    }

    /// <returns>
    /// The closing events (a normal finish, or an error event when the upstream failed, stalled or stopped early), the
    /// fault of an error reported before anything was written, or the message of a request error reported before then.
    /// </returns>
    private async Task<(string? Closing, ModelFault? Early, string? Rejection)> TranslateChunksAsync(ChatStreamTranslator translator, Func<string, Task> write,
        Func<bool> started, StreamStartGrace grace, HttpResponseMessage response, CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        var idleTimeout = options.Value.Local.StreamIdleTimeout;
        var data = new List<string>();
        while (true)
        {
            string? line;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(idleTimeout);
                try
                {
                    line = await grace.AwaitAsync(reader.ReadLineAsync(idle.Token).AsTask(), ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return (translator.Abort($"Local model sent nothing for {idleTimeout.TotalSeconds:0.#} s."), null, null);
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
            // A blank line, or the end of the body, dispatches the pending event; its data lines join with newlines.
            if (data.Count > 0)
            {
                var payload = string.Join("\n", data).Trim();
                data.Clear();
                if (await DispatchAsync(translator, payload, write, started) is { } ended)
                {
                    return ended;
                }
            }
            if (line is null)
            {
                return (translator.Finished ? translator.Finish() : translator.Abort("Local model stream ended before completion."), null, null);
            }
        }
    }

    /// <returns>Null after writing the event's translation, else how the stream ends (see <see cref="TranslateChunksAsync"/>).</returns>
    private static async Task<(string? Closing, ModelFault? Early, string? Rejection)?> DispatchAsync(ChatStreamTranslator translator, string payload,
        Func<string, Task> write, Func<bool> started)
    {
        if (payload == "[DONE]")
        {
            return (translator.Finish(), null, null);
        }
        JsonObject? chunk;
        try
        {
            chunk = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            chunk = null;
        }
        if (chunk is null)
        {
            return (translator.Abort("Local model sent a malformed stream chunk."), null, null);
        }
        if (chunk["error"] is { } error)
        {
            var message = ErrorMessage(error);
            if (!started())
            {
                // Nothing was produced yet: the prompt not fitting, the request itself, or the endpoint failing; not the answer.
                if (ContextOverflow.IsReported(message))
                {
                    return (null, new ModelFault(FaultKind.ContextOverflow, Message: message), null);
                }
                if (IsRequestError(error))
                {
                    return (null, null, message ?? "Local model rejected the request.");
                }
                return (null, new ModelFault(FaultKind.Overloaded), null);
            }
            return (translator.Abort(message ?? "Local model reported an error mid-stream."), null, null);
        }
        await write(translator.Feed(chunk));
        return null;
    }

    private async Task<UpstreamResult> ErrorAsync(HttpContext context, CatalogModel model, HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var retryAfter = LimitHeaders.RetryAfter(response.Headers, time.GetUtcNow());
        if (status is 429 or >= 500)
        {
            logger.LogWarning("Local model {model} answered HTTP {status}; not serving this request from it", model.Id, status);
            return UpstreamResult.NotServed(new ModelFault(status == 429 ? FaultKind.Limited : FaultKind.Overloaded, retryAfter));
        }
        var (clientStatus, type) = status switch
        {
            400 or 422 => (StatusCodes.Status400BadRequest, "invalid_request_error"),
            413 => (StatusCodes.Status413PayloadTooLarge, "request_too_large"),
            // An auth failure is the router's local configuration, not the client's request.
            _ => (StatusCodes.Status502BadGateway, "api_error"),
        };

        // Read at most one character past the limit, so an oversized or endless body is never buffered whole.
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        var buffer = new char[MaxErrorBodyChars + 1];
        var length = await reader.ReadBlockAsync(buffer, ct);
        string? message = null;
        try
        {
            message = ErrorMessage(JsonNode.Parse(length > MaxErrorBodyChars ? "" : new string(buffer, 0, length))?["error"]);
        }
        catch (JsonException)
        {
        }
        if (status is 400 or 413 or 422 && ContextOverflow.IsReported(message))
        {
            logger.LogWarning("Local model {model} rejected the request as too long for its context", model.Id);
            return UpstreamResult.NotServed(new ModelFault(FaultKind.ContextOverflow, Message: message));
        }
        await AnthropicError.WriteAsync(context, clientStatus, type, message ?? $"Local model {model.Id} returned HTTP {status}.");
        return UpstreamResult.Served;
    }

    /// <summary>An error typed or coded as the request's fault (vLLM <c>BadRequestError</c>, llama.cpp <c>invalid_request_error</c>, code 400/422).</summary>
    private static bool IsRequestError(JsonNode error)
    {
        if (error is not JsonObject obj)
        {
            return false;
        }
        var type = ChatRequestTranslator.Str(obj["type"]);
        var code = obj["code"]?.ToString();
        return type is "invalid_request_error" or "BadRequestError" || code is "400" or "422";
    }

    private static string? ErrorMessage(JsonNode? error) =>
        error is JsonObject obj ? ChatRequestTranslator.Str(obj["message"]) : ChatRequestTranslator.Str(error);
}
