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
/// non-streaming client gets the stream folded into one message.
/// </summary>
public sealed class CodexUpstream(
    IHttpClientFactory httpClientFactory,
    CodexTokenProvider tokens,
    IOptions<RouterOptions> options,
    ILogger<CodexUpstream> logger) : IModelUpstream
{
    public const string HttpClientName = "codex-upstream";
    private const int MaxErrorBodyChars = 64 * 1024;

    // The Codex backend only serves clients that identify as the Codex CLI.
    private const string Originator = "codex_cli_rs";

    public ProviderKind Provider => ProviderKind.Codex;

    public async Task SendAsync(UpstreamCall call)
    {
        var context = call.Context;
        var ct = context.RequestAborted;
        if (call.CountTokens)
        {
            await context.Response.WriteAsJsonAsync(new { input_tokens = call.EstimatedInputTokens }, ct);
            return;
        }

        CodexAccess access;
        try
        {
            access = await tokens.GetAccessAsync(ct);
        }
        catch (CodexNotLoggedInException e)
        {
            await AnthropicError.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "api_error", e.Message);
            return;
        }
        catch (CodexOAuthException e)
        {
            logger.LogWarning("Codex access token refresh failed with status {status}", e.Status);
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", e.Message);
            return;
        }

        var cacheKey = new Guid(Convert.FromHexString(call.Session.Key[..32])).ToString();
        var body = ResponsesRequestTranslator.Translate(call.Request, call.Model, cacheKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.Codex.ApiBaseUrl.TrimEnd('/') + "/responses")
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, WireJson.Relaxed)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", access.AccountId);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.TryAddWithoutValidation("originator", Originator);
        request.Headers.TryAddWithoutValidation("User-Agent", Originator);
        request.Headers.TryAddWithoutValidation("session_id", cacheKey);
        request.Headers.Accept.ParseAdd("text/event-stream");

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning("Codex upstream request failed: {error}", e.Message);
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", "Codex upstream is unreachable.");
            return;
        }

        using (response)
        {
            var translator = new ResponsesStreamTranslator(call.Model.Id, call.EstimatedInputTokens);
            if (!response.IsSuccessStatusCode)
            {
                await WriteUpstreamErrorAsync(context, response, ct);
            }
            else if (call.Request["stream"] is JsonValue s && s.TryGetValue<bool>(out var streaming) && streaming)
            {
                await StreamAsync(context, translator, response, ct);
            }
            else
            {
                await AggregateAsync(context, translator, response, ct);
            }
        }
    }

    private async Task StreamAsync(HttpContext context, ResponsesStreamTranslator translator, HttpResponseMessage response, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await WriteAsync(context, [translator.Start()], ct);
        await TranslateAsync(translator, response, events => WriteAsync(context, events, ct), ct);
    }

    private async Task AggregateAsync(HttpContext context, ResponsesStreamTranslator translator, HttpResponseMessage response, CancellationToken ct)
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
            await AnthropicError.WriteAsync(context, CodexErrors.Status(type), type, ChatRequestTranslator.Str(error["message"]) ?? "Codex response failed.");
            return;
        }
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(builder.Message, WireJson.Relaxed), ct);
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

    /// <summary>Keeps the upstream status (a 429 stays a 429) with an Anthropic-shaped body.</summary>
    private static async Task WriteUpstreamErrorAsync(HttpContext context, HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        // A rejected subscription credential is the router's to fix; passed through, the client would ask for its own login.
        var clientStatus = status is 401 or 403 ? StatusCodes.Status502BadGateway : status;
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString();
        }
        // Read at most one character past the limit, so an oversized or endless body is never buffered whole.
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        var buffer = new char[MaxErrorBodyChars + 1];
        var length = await reader.ReadBlockAsync(buffer, ct);
        string? message = null;
        try
        {
            var error = JsonNode.Parse(length > MaxErrorBodyChars ? "" : new string(buffer, 0, length));
            message = ChatRequestTranslator.Str(error?["error"]?["message"]) ?? ChatRequestTranslator.Str(error?["detail"]);
        }
        catch (JsonException)
        {
        }
        if (clientStatus != status)
        {
            message = $"Codex rejected the subscription credentials (HTTP {status}); run 'simple-router login codex'.";
        }
        await AnthropicError.WriteAsync(context, clientStatus, CodexErrors.TypeForStatus(clientStatus), message ?? $"Codex returned HTTP {status}.");
    }
}
