using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SimpleRouter.Api;
using SimpleRouter.Local;
using SimpleRouter.Routing;
using SimpleRouter.Usage;

namespace SimpleRouter.OpenRouter;

/// <summary>Provider-key authenticated Chat Completions without local health checks or capacity holds.</summary>
public sealed class OpenRouterUpstream(
    IHttpClientFactory httpClientFactory,
    OpenRouterCredentialStore credentials,
    IOptions<RouterOptions> options,
    TimeProvider time,
    ILogger<OpenRouterUpstream> logger) : IModelUpstream
{
    public const string HttpClientName = "openrouter-upstream";
    public ProviderKind Provider => ProviderKind.OpenRouter;

    public async Task<UpstreamResult> SendAsync(UpstreamCall call)
    {
        var context = call.Context;
        var ct = context.RequestAborted;
        if (call.CountTokens)
        {
            await context.Response.WriteAsJsonAsync(new { input_tokens = call.EstimatedInputTokens }, ct);
            return UpstreamResult.Served;
        }
        var key = await credentials.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(key)) return UpstreamResult.NotServed(new ModelFault(FaultKind.AuthRejected));
        var chat = OpenRouterRequest.Translate(call);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.OpenRouter.ApiBaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(chat, WireJson.Relaxed)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            RequestDiagnostics.Fail(context, e is OperationCanceledException ? "upstream_timeout" : "upstream_unreachable");
            logger.LogWarning("OpenRouter model {model} is unreachable", call.Model.Id);
            return UpstreamResult.NotServed(new ModelFault(FaultKind.Unreachable));
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                RequestDiagnostics.Fail(context, "http_error");
                var status = (int)response.StatusCode;
                if (status is 401 or 402 or 403 or 404 or 408 or 429 or >= 500)
                    return UpstreamResult.NotServed(new ModelFault(Fault(status), LimitHeaders.RetryAfter(response.Headers, time.GetUtcNow())));
                try
                {
                    if (status is 400 or 413 or 422 && await IsContextOverflowAsync(response, ct))
                        return UpstreamResult.NotServed(new ModelFault(FaultKind.ContextOverflow));
                }
                catch (Exception e) when (e is IOException or HttpRequestException)
                {
                    return UpstreamResult.NotServed(new ModelFault(FaultKind.Unreachable));
                }
                // Provider error bodies may contain echoed prompts or credential information; never relay them.
                await AnthropicError.WriteAsync(context, status is 400 or 422 ? 400 : status == 413 ? 413 : 502,
                    status is 400 or 422 ? "invalid_request_error" : "api_error", $"OpenRouter rejected the request (HTTP {status}).");
                return UpstreamResult.Served;
            }
            var transport = new ChatResponseTransport(options.Value.Limits, options.Value.OpenRouter.StreamIdleTimeout,
                time, logger, openRouter: true, capture: UpstreamUsageMetadata.Capture);
            if (chat["stream"] is not null)
            {
                var fault = await transport.StreamAsync(context, call.Model, call.EstimatedInputTokens, response, ct);
                return fault is null ? UpstreamResult.Served : UpstreamResult.NotServed(fault);
            }
            JsonObject? raw;
            try
            {
                raw = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct) as JsonObject;
            }
            catch (Exception e) when (e is JsonException or IOException or HttpRequestException)
            {
                RequestDiagnostics.Fail(context, e is JsonException ? "malformed_response" : "transport_error");
                raw = null;
            }
            if (raw is null)
            {
                if (RequestDiagnostics.Get(context) is null) RequestDiagnostics.Fail(context, "malformed_response");
                return UpstreamResult.NotServed(new ModelFault(FaultKind.Overloaded));
            }
            UpstreamUsageMetadata.Capture(context, raw);
            if (raw["error"] is { } error)
            {
                RequestDiagnostics.Fail(context, "upstream_error");
                var status = error is JsonObject obj && int.TryParse(obj["code"]?.ToString(), out var code) ? code : 502;
                return UpstreamResult.NotServed(new ModelFault(ErrorFault(error, status)));
            }
            if ((raw["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice
                || choice["message"] is not JsonObject)
            {
                RequestDiagnostics.Fail(context, "malformed_response");
                return UpstreamResult.NotServed(new ModelFault(FaultKind.Overloaded));
            }
            if (ChatRequestTranslator.Str(choice["finish_reason"]) == "error")
            {
                RequestDiagnostics.Fail(context, "upstream_error");
                return UpstreamResult.NotServed(new ModelFault(FaultKind.Overloaded));
            }
            byte[] translated;
            try
            {
                translated = JsonSerializer.SerializeToUtf8Bytes(
                    ChatResponseTranslator.Translate(raw, call.Model.Id, call.EstimatedInputTokens, openRouter: true), WireJson.Relaxed);
            }
            catch (InvalidOperationException)
            {
                RequestDiagnostics.Fail(context, "malformed_response");
                return UpstreamResult.NotServed(new ModelFault(FaultKind.Overloaded));
            }
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(translated, ct);
            return UpstreamResult.Served;
        }
    }

    private static async Task<bool> IsContextOverflowAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var buffer = new char[64 * 1024 + 1];
        var length = await reader.ReadBlockAsync(buffer, ct);
        if (length == buffer.Length) return false;
        try
        {
            return JsonNode.Parse(new string(buffer, 0, length)) is JsonObject body
                && ErrorFault(body["error"], 400) == FaultKind.ContextOverflow;
        }
        catch (JsonException) { return false; }
    }

    internal static FaultKind ErrorFault(JsonNode? error, int status)
    {
        if (error is JsonObject obj && (ChatRequestTranslator.Str(obj["code"]) == "context_length_exceeded"
            || ContextOverflow.IsReported(ChatRequestTranslator.Str(obj["message"]))))
            return FaultKind.ContextOverflow;
        return Fault(status);
    }

    internal static FaultKind Fault(int status) => status switch
    {
        401 or 402 or 403 => FaultKind.AuthRejected,
        404 => FaultKind.PlanRejected,
        429 => FaultKind.Limited,
        408 => FaultKind.Unreachable,
        _ => FaultKind.Overloaded,
    };
}
