using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using SimpleRouter.Api;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.OpenRouter;
using SimpleRouter.Usage;

namespace SimpleRouter.Local;

/// <summary>Shared Chat Completions response translation, start grace, keep-alives and idle timeout.</summary>
public sealed class ChatResponseTransport(
    LimitOptions limits,
    TimeSpan streamIdleTimeout,
    TimeProvider time,
    ILogger logger,
    bool openRouter = false,
    Action<HttpContext, JsonObject>? capture = null)
{
    private string Label => openRouter ? "OpenRouter model" : "Local model";

    public async Task WriteMessageAsync(HttpContext context, CatalogModel model, int estimate, HttpResponseMessage response, CancellationToken ct)
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
            RequestDiagnostics.Fail(context, "malformed_response");
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", $"{Label} {model.Id} returned an unreadable response.");
            return;
        }
        if (chat["error"] is not null)
        {
            RequestDiagnostics.Fail(context, "upstream_error");
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", $"{Label} reported an error.");
            return;
        }
        if ((chat["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice || choice["message"] is not JsonObject)
        {
            RequestDiagnostics.Fail(context, "malformed_response");
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", $"{Label} returned an invalid response.");
            return;
        }
        if (ChatRequestTranslator.Str(choice["finish_reason"]) == "error")
        {
            RequestDiagnostics.Fail(context, "upstream_error");
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", $"{Label} reported an error.");
            return;
        }
        capture?.Invoke(context, chat);
        byte[] translated;
        try
        {
            translated = JsonSerializer.SerializeToUtf8Bytes(ChatResponseTranslator.Translate(chat, model.Id, estimate, openRouter), WireJson.Relaxed);
        }
        catch (InvalidOperationException)
        {
            RequestDiagnostics.Fail(context, "malformed_response");
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", $"{Label} returned an invalid response.");
            return;
        }
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(translated, ct);
    }

    /// <summary>
    /// Streams the translation, withholding <c>message_start</c> until the first output or closing event (or the end of the
    /// <see cref="StreamStartGrace"/>), so an error the endpoint reports before any output leaves the response untouched for
    /// another model, or is answered as the client's request error.
    /// </summary>
    /// <returns>Null once written; else why nothing was.</returns>
    public async Task<ModelFault?> StreamAsync(HttpContext context, CatalogModel model, int estimate, HttpResponseMessage response, CancellationToken ct)
    {
        var translator = new ChatStreamTranslator(model.Id, estimate, openRouter);
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
        await using var output = new StreamKeepAlive(context.Response.Body, limits.KeepAliveInterval, time, ct);
        async Task Write(string sse, bool final)
        {
            if (sse.Length == 0)
            {
                return;
            }
            await output.WriteAsync(Encoding.UTF8.GetBytes(started ? sse : Begin(sse)), final);
        }
        var grace = new StreamStartGrace(limits.StreamStartGrace, time, async () =>
        {
            if (!started)
            {
                await Write(Begin(""), final: false);
            }
        });

        string tail;
        try
        {
            var (closing, early, rejection) = await TranslateChunksAsync(context, translator, sse => Write(sse, final: false), () => started, grace, response, ct);
            if (early is not null)
            {
                return openRouter ? early with { ResetAt = early.ResetAt ?? LimitHeaders.RetryAfter(response.Headers, time.GetUtcNow()) } : early;
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
            RequestDiagnostics.Fail(context, "transport_error");
            logger.LogWarning("Chat model {model} stream failed", model.Id);
            if (openRouter && !started) return new ModelFault(FaultKind.Unreachable);
            tail = translator.Abort($"{Label} {model.Id} stream was interrupted.");
        }
        await Write(tail, final: true);
        return null;
    }

    /// <returns>
    /// The closing events (a normal finish, or an error event when the upstream failed, stalled or stopped early), the
    /// fault of an error reported before anything was written, or the message of a request error reported before then.
    /// </returns>
    private async Task<(string? Closing, ModelFault? Early, string? Rejection)> TranslateChunksAsync(HttpContext context, ChatStreamTranslator translator, Func<string, Task> write,
        Func<bool> started, StreamStartGrace grace, HttpResponseMessage response, CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        var idleTimeout = streamIdleTimeout;
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
                    RequestDiagnostics.Fail(context, "upstream_timeout");
                    if (openRouter && !started()) return (null, new ModelFault(FaultKind.Unreachable), null);
                    return (translator.Abort($"{Label} sent nothing for {idleTimeout.TotalSeconds:0.#} s."), null, null);
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
                if (await DispatchAsync(context, translator, payload, write, started) is { } ended)
                {
                    return ended;
                }
            }
            if (line is null)
            {
                if (!translator.Finished) RequestDiagnostics.Fail(context, "truncated_stream");
                if (openRouter && !started() && !translator.Finished) return (null, new ModelFault(FaultKind.Overloaded), null);
                return (translator.Finished ? translator.Finish() : translator.Abort($"{Label} stream ended before completion."), null, null);
            }
        }
    }

    /// <returns>Null after writing the event's translation, else how the stream ends (see <see cref="TranslateChunksAsync"/>).</returns>
    private async Task<(string? Closing, ModelFault? Early, string? Rejection)?> DispatchAsync(HttpContext context, ChatStreamTranslator translator, string payload,
        Func<string, Task> write, Func<bool> started)
    {
        if (payload == "[DONE]")
        {
            if (!translator.Finished)
            {
                RequestDiagnostics.Fail(context, "truncated_stream");
                return openRouter && !started() ? (null, new ModelFault(FaultKind.Overloaded), null)
                    : (translator.Abort($"{Label} stream ended before completion."), null, null);
            }
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
            RequestDiagnostics.Fail(context, "malformed_response");
            if (openRouter && !started()) return (null, new ModelFault(FaultKind.Overloaded), null);
            return (translator.Abort($"{Label} sent a malformed stream chunk."), null, null);
        }
        capture?.Invoke(context, chunk);
        if (ChatRequestTranslator.Str((chunk["choices"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["finish_reason"]) == "error" && chunk["error"] is null)
            chunk["error"] = new JsonObject { ["code"] = 502 };
        if (chunk["error"] is { } error)
        {
            RequestDiagnostics.Fail(context, "upstream_error");
            if (openRouter)
            {
                var code = error is JsonObject obj && int.TryParse(obj["code"]?.ToString(), out var status) ? status : 502;
                return started() ? (translator.Abort("OpenRouter reported a stream error."), null, null)
                    : (null, new ModelFault(OpenRouterUpstream.ErrorFault(error, code)), null);
            }
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
                    return (null, null, message ?? $"{Label} rejected the request.");
                }
                return (null, new ModelFault(FaultKind.Overloaded), null);
            }
            return (translator.Abort(message ?? $"{Label} reported an error mid-stream."), null, null);
        }
        if (chunk["choices"] is not JsonArray choices || (choices.Count == 0 && chunk["usage"] is not JsonObject)
            || (choices.Count > 0 && choices[0] is not JsonObject))
        {
            RequestDiagnostics.Fail(context, "malformed_response");
            if (openRouter && !started()) return (null, new ModelFault(FaultKind.Overloaded), null);
            return (translator.Abort($"{Label} sent an invalid stream chunk."), null, null);
        }
        string translated;
        try
        {
            translated = translator.Feed(chunk);
        }
        catch (InvalidOperationException)
        {
            RequestDiagnostics.Fail(context, "malformed_response");
            if (openRouter && !started()) return (null, new ModelFault(FaultKind.Overloaded), null);
            return (translator.Abort($"{Label} sent an invalid stream chunk."), null, null);
        }
        await write(translated);
        return null;
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
