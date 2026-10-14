using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using SimpleRouter.Api;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.Local;

/// <summary>Serves Messages API calls from a local OpenAI-compatible Chat Completions endpoint, translating both ways.</summary>
public sealed class LocalUpstream(
    IHttpClientFactory httpClientFactory,
    SecretProtector secrets,
    LocalHealth health,
    Microsoft.Extensions.Options.IOptions<RouterOptions> options,
    ILogger<LocalUpstream> logger) : IModelUpstream
{
    public const string HttpClientName = "local-upstream";
    private const int MaxErrorBodyChars = 64 * 1024;

    public ProviderKind Provider => ProviderKind.Local;

    public static string Endpoint(CatalogModel model, string path) =>
        (model.BaseUrl ?? throw new InvalidOperationException($"Local model {model.Id} has no base URL.")).TrimEnd('/') + "/" + path;

    public async Task SendAsync(UpstreamCall call)
    {
        var (context, model) = (call.Context, call.Model);
        var ct = context.RequestAborted;
        if (call.CountTokens)
        {
            await context.Response.WriteAsJsonAsync(new { input_tokens = call.EstimatedInputTokens }, ct);
            return;
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
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", $"Local model {model.Id} is unreachable.");
            return;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                await WriteUpstreamErrorAsync(context, model, response, ct);
            }
            else if (chat["stream"] is not null)
            {
                await StreamAsync(context, model, call.EstimatedInputTokens, response, ct);
            }
            else
            {
                await WriteMessageAsync(context, model, call.EstimatedInputTokens, response, ct);
            }
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

    private async Task StreamAsync(HttpContext context, CatalogModel model, int estimate, HttpResponseMessage response, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var translator = new ChatStreamTranslator(model.Id, estimate);
        await WriteAsync(context, translator.Start(), ct);
        string tail;
        try
        {
            tail = await TranslateChunksAsync(context, translator, response, ct);
        }
        catch (Exception e) when (e is IOException or HttpRequestException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Local model {model} stream failed: {error}", model.Id, e.Message);
            tail = translator.Abort($"Local model {model.Id} stream was interrupted.");
        }
        await WriteAsync(context, tail, ct);
    }

    /// <returns>The closing events: a normal finish, or an error event when the upstream failed, stalled or stopped early.</returns>
    private async Task<string> TranslateChunksAsync(HttpContext context, ChatStreamTranslator translator, HttpResponseMessage response, CancellationToken ct)
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
                    line = await reader.ReadLineAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return translator.Abort($"Local model sent nothing for {idleTimeout.TotalSeconds:0.#} s.");
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
                if (await DispatchAsync(context, translator, payload, ct) is { } closing)
                {
                    return closing;
                }
            }
            if (line is null)
            {
                return translator.Finished ? translator.Finish() : translator.Abort("Local model stream ended before completion.");
            }
        }
    }

    /// <returns>The closing events when the event ends the stream, else null after writing its translation.</returns>
    private static async Task<string?> DispatchAsync(HttpContext context, ChatStreamTranslator translator, string payload, CancellationToken ct)
    {
        if (payload == "[DONE]")
        {
            return translator.Finish();
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
            return translator.Abort("Local model sent a malformed stream chunk.");
        }
        if (chunk["error"] is { } error)
        {
            return translator.Abort(ErrorMessage(error) ?? "Local model reported an error mid-stream.");
        }
        await WriteAsync(context, translator.Feed(chunk), ct);
        return null;
    }

    private static async Task WriteAsync(HttpContext context, string sse, CancellationToken ct)
    {
        if (sse.Length == 0)
        {
            return;
        }
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(sse), ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static async Task WriteUpstreamErrorAsync(HttpContext context, CatalogModel model, HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var (clientStatus, type) = status switch
        {
            400 or 422 => (StatusCodes.Status400BadRequest, "invalid_request_error"),
            413 => (StatusCodes.Status413PayloadTooLarge, "request_too_large"),
            429 => (StatusCodes.Status429TooManyRequests, "rate_limit_error"),
            // An auth or server failure is the router's local configuration, not the client's request.
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
        await AnthropicError.WriteAsync(context, clientStatus, type, message ?? $"Local model {model.Id} returned HTTP {status}.");
    }

    private static string? ErrorMessage(JsonNode? error) =>
        error is JsonObject obj ? ChatRequestTranslator.Str(obj["message"]) : ChatRequestTranslator.Str(error);
}
