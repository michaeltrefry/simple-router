using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SimpleRouter.Api;
using SimpleRouter.Local;
using SimpleRouter.Routing;
using SimpleRouter.Usage;

namespace SimpleRouter.RouterCom;

/// <summary>Router.com Responses supports explicit Flex preferences, with shared key auth and tier failover.</summary>
public sealed class RouterComUpstream(
    IHttpClientFactory httpClientFactory, RouterComCredentialStore credentials, IOptions<RouterOptions> options,
    TimeProvider time, ILogger<RouterComUpstream> logger) : IModelUpstream
{
    public const string HttpClientName = "routercom-upstream";
    public ProviderKind Provider => ProviderKind.RouterCom;

    public async Task<UpstreamResult> SendAsync(UpstreamCall call)
    {
        var context = call.Context;
        var ct = context.RequestAborted;
        if (call.CountTokens) return await EstimateAsync(call);
        var key = await credentials.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(key)) return UpstreamResult.NotServed(new ModelFault(FaultKind.AuthRejected));
        var body = RouterComRequest.Translate(call);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.RouterCom.ApiBaseUrl.TrimEnd('/') + "/responses")
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, WireJson.Relaxed)),
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
            logger.LogWarning("Router.com model {model} is unreachable", call.Model.Id);
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
                if (status is 400 or 413 or 422)
                {
                    try
                    {
                        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                        var head = new char[64 * 1024 + 1];
                        var length = await reader.ReadBlockAsync(head, ct);
                        if (length < head.Length && JsonNode.Parse(new string(head, 0, length)) is JsonObject error
                            && ContextOverflow.IsReported(ChatRequestTranslator.Str(error["error"]?["message"])))
                            return UpstreamResult.NotServed(new ModelFault(FaultKind.ContextOverflow));
                    }
                    catch (JsonException) { }
                    catch (Exception e) when (e is IOException or HttpRequestException)
                    {
                        return UpstreamResult.NotServed(new ModelFault(FaultKind.Unreachable));
                    }
                }
                await AnthropicError.WriteAsync(context, status is 400 or 422 ? 400 : status == 413 ? 413 : 502,
                    status is 400 or 422 ? "invalid_request_error" : "api_error", $"Router.com rejected the request (HTTP {status}).");
                return UpstreamResult.Served;
            }
            return await new RouterComResponses(options.Value, time).SendAsync(call, response);
        }
    }

    private static async Task<UpstreamResult> EstimateAsync(UpstreamCall call)
    {
        await call.Context.Response.WriteAsJsonAsync(new { input_tokens = call.EstimatedInputTokens }, call.Context.RequestAborted);
        return UpstreamResult.Served;
    }

    internal static FaultKind Fault(int status) => status switch
    {
        401 or 402 => FaultKind.AuthRejected,
        403 or 404 or 501 => FaultKind.PlanRejected,
        429 => FaultKind.Limited,
        408 => FaultKind.Unreachable,
        _ => FaultKind.Overloaded,
    };
}
