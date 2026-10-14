using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using SimpleRouter.Api;
using SimpleRouter.Routing;

namespace SimpleRouter.Claude;

/// <summary>Forwards a Messages API body to Claude with the subscription token and streams the response back unchanged.</summary>
public sealed class ClaudeUpstream(
    IHttpClientFactory httpClientFactory,
    ClaudeTokenProvider tokens,
    IOptions<RouterOptions> options,
    ILogger<ClaudeUpstream> logger) : IModelUpstream
{
    public const string HttpClientName = "claude-upstream";
    public const string OAuthBeta = "oauth-2025-04-20";
    private const string DefaultAnthropicVersion = "2023-06-01";
    private const int CopyBufferBytes = 16 * 1024;

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    public ProviderKind Provider => ProviderKind.Claude;

    public Task SendAsync(UpstreamCall call)
    {
        call.Request["model"] = call.Model.UpstreamModelId;
        DropUnsignedThinking(call.Request);
        var path = call.CountTokens ? "/v1/messages/count_tokens" : "/v1/messages";
        return ForwardAsync(call.Context, path, JsonSerializer.SerializeToUtf8Bytes(call.Request, WireJson.Relaxed), injectIdentity: !call.CountTokens);
    }

    /// <summary>
    /// Thinking blocks served by another provider carry no signature, and Anthropic rejects the whole request over one,
    /// so a session that moves to Claude drops them from its history.
    /// </summary>
    private static void DropUnsignedThinking(JsonObject request)
    {
        foreach (var message in request["messages"] as JsonArray ?? [])
        {
            if (message?["content"] is not JsonArray blocks)
            {
                continue;
            }
            foreach (var block in blocks.Where(IsUnsignedThinking).ToList())
            {
                blocks.Remove(block);
            }
        }
    }

    private static bool IsUnsignedThinking(JsonNode? block) =>
        block?["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "thinking"
        && !(block["signature"] is JsonValue s && s.TryGetValue<string>(out var signature) && signature.Length > 0);

    private async Task ForwardAsync(HttpContext context, string path, byte[] body, bool injectIdentity)
    {
        var ct = context.RequestAborted;
        string accessToken;
        try
        {
            accessToken = await tokens.GetAccessTokenAsync(ct);
        }
        catch (ClaudeNotLoggedInException e)
        {
            await AnthropicError.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "api_error", e.Message);
            return;
        }
        catch (ClaudeOAuthException e)
        {
            logger.LogWarning("Claude access token refresh failed with status {status}", e.Status);
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", e.Message);
            return;
        }

        if (injectIdentity)
        {
            body = ClaudeIdentity.Ensure(body);
        }

        using var request = BuildRequest(context.Request, path, body, accessToken);
        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning("Claude upstream request failed: {error}", e.Message);
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", "Claude upstream is unreachable.");
            return;
        }

        using (response)
        {
            await CopyResponseAsync(response, context, ct);
        }
    }

    private HttpRequestMessage BuildRequest(HttpRequest inbound, string path, byte[] body, string accessToken)
    {
        var url = options.Value.Claude.ApiBaseUrl.TrimEnd('/') + path + inbound.QueryString;
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(inbound.ContentType ?? "application/json");

        foreach (var (name, values) in inbound.Headers)
        {
            if (name.StartsWith("anthropic-", StringComparison.OrdinalIgnoreCase) || name.Equals("Accept", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
        if (!request.Headers.Contains("anthropic-version"))
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", DefaultAnthropicVersion);
        }
        request.Headers.Remove("anthropic-beta");
        request.Headers.TryAddWithoutValidation("anthropic-beta", MergeBeta(inbound.Headers["anthropic-beta"].ToString()));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static string MergeBeta(string existing)
    {
        var flags = existing.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!flags.Contains(OAuthBeta))
        {
            flags.Add(OAuthBeta);
        }
        return string.Join(",", flags);
    }

    private static async Task CopyResponseAsync(HttpResponseMessage upstream, HttpContext context, CancellationToken ct)
    {
        var response = context.Response;
        response.StatusCode = (int)upstream.StatusCode;
        foreach (var (name, values) in upstream.Headers.Concat(upstream.Content.Headers))
        {
            if (!HopByHopHeaders.Contains(name))
            {
                response.Headers[name] = values.ToArray();
            }
        }
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await using var stream = await upstream.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[CopyBufferBytes];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            await response.Body.FlushAsync(ct);
        }
    }
}
