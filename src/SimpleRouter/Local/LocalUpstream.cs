using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimpleRouter.Api;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Usage;

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
        catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            RequestDiagnostics.Fail(context, e is OperationCanceledException ? "upstream_timeout" : "upstream_unreachable");
            logger.LogWarning("Local model {model} is unreachable", model.Id);
            health.MarkUnhealthy(model.Id);
            return UpstreamResult.NotServed(new ModelFault(FaultKind.Unreachable));
        }

        var transport = new ChatResponseTransport(options.Value.Limits, options.Value.Local.StreamIdleTimeout, time, logger);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return await ErrorAsync(context, model, response, ct);
            }
            if (chat["stream"] is not null)
            {
                if (await transport.StreamAsync(context, model, call.EstimatedInputTokens, response, ct) is { } fault)
                {
                    logger.LogWarning("Local model {model} failed before any output; not serving this request from it ({reason})", model.Id, fault.Kind);
                    return UpstreamResult.NotServed(fault);
                }
            }
            else
            {
                await transport.WriteMessageAsync(context, model, call.EstimatedInputTokens, response, ct);
            }
            return UpstreamResult.Served;
        }
    }

    private async Task<UpstreamResult> ErrorAsync(HttpContext context, CatalogModel model, HttpResponseMessage response, CancellationToken ct)
    {
        RequestDiagnostics.Fail(context, "http_error");
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
