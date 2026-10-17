using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using SimpleRouter.Api;
using SimpleRouter.Codex;
using SimpleRouter.Local;
using SimpleRouter.Routing;

namespace SimpleRouter.Claude;

/// <summary>
/// Forwards a Messages API body to Claude with the subscription token and streams the response back unchanged. A limit,
/// plan or account rejection, overload or connection failure on a message is returned unwritten so the router can fail
/// over; token counting is passed through, or answered with the local estimate when the token cannot be refreshed or is rejected.
/// </summary>
public sealed class ClaudeUpstream(
    IHttpClientFactory httpClientFactory,
    ClaudeTokenProvider tokens,
    IOptions<RouterOptions> options,
    TimeProvider time,
    ILogger<ClaudeUpstream> logger) : IModelUpstream
{
    public const string HttpClientName = "claude-upstream";
    public const string OAuthBeta = "oauth-2025-04-20";
    internal const string DefaultAnthropicVersion = "2023-06-01";
    private const int CopyBufferBytes = 16 * 1024;
    private const int MaxErrorBodyBytes = 64 * 1024;

    private static readonly Regex PlanRefusal = new("model|plan|access to", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    public ProviderKind Provider => ProviderKind.Claude;

    public Task<UpstreamResult> SendAsync(UpstreamCall call)
    {
        call.Request["model"] = call.Model.UpstreamModelId;
        DropForeignThinking(call.Request);
        var path = call.CountTokens ? "/v1/messages/count_tokens" : "/v1/messages";
        return ForwardAsync(call, path, JsonSerializer.SerializeToUtf8Bytes(call.Request, WireJson.Relaxed));
    }

    /// <summary>
    /// Thinking blocks served by another provider carry no Anthropic signature (none, Codex's encrypted reasoning or a local marker),
    /// and Anthropic rejects the whole request over one, so a session that moves to Claude drops them from its history.
    /// </summary>
    private static void DropForeignThinking(JsonObject request)
    {
        var messages = request["messages"] as JsonArray ?? [];
        foreach (var blocks in messages.Select(m => m?["content"]).OfType<JsonArray>())
        {
            var foreign = blocks.Where(IsForeignThinking).ToList();
            foreach (var block in foreign)
            {
                blocks.Remove(block);
            }
            if (foreign.Count > 0 && blocks.Count == 0)
            {
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = "[reasoning omitted]" });
            }
        }

        // With thinking on, Anthropic rejects a tool loop whose last assistant turn does not start with thinking.
        if (messages.LastOrDefault(m => Str(m?["role"]) == "assistant")?["content"] is JsonArray last
            && last.Any(b => Str(b?["type"]) == "tool_use")
            && Str(last.FirstOrDefault()?["type"]) is not ("thinking" or "redacted_thinking"))
        {
            request.Remove("thinking");
        }
    }

    private static bool IsForeignThinking(JsonNode? block) =>
        Str(block?["type"]) == "thinking"
        && (Str(block!["signature"]) is not { Length: > 0 } signature
            || signature.StartsWith(ResponsesStreamTranslator.SignaturePrefix, StringComparison.Ordinal)
            || signature.StartsWith(ChatResponseTranslator.SignaturePrefix, StringComparison.Ordinal));

    private static string? Str(JsonNode? node) => ChatRequestTranslator.Str(node);

    private async Task<UpstreamResult> ForwardAsync(UpstreamCall call, string path, byte[] body)
    {
        var (context, message) = (call.Context, !call.CountTokens);
        var ct = context.RequestAborted;
        if (message)
        {
            body = ClaudeIdentity.Ensure(body);
        }

        HttpResponseMessage response;
        try
        {
            string accessToken;
            (response, accessToken) = await SendOnceAsync(context, path, body, rejected: null);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The access token may have been revoked before its expiry; one forced refresh decides.
                response.Dispose();
                (response, _) = await SendOnceAsync(context, path, body, rejected: accessToken);
            }
        }
        catch (ClaudeNotLoggedInException e)
        {
            await AnthropicError.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "api_error", e.Message);
            return UpstreamResult.Served;
        }
        catch (ClaudeOAuthException e)
        {
            logger.LogWarning("Claude access token refresh failed with status {status}", e.Status);
            if (message)
            {
                return UpstreamResult.NotServed(ModelFault.RefreshFailed(e.Status));
            }
            return await EstimateAsync(call);
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning("Claude upstream request failed: {error}", e.Message);
            if (message)
            {
                return UpstreamResult.NotServed(new ModelFault(FaultKind.Unreachable));
            }
            await AnthropicError.WriteAsync(context, StatusCodes.Status502BadGateway, "api_error", "Claude upstream is unreachable.");
            return UpstreamResult.Served;
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (!message && status is 401 or 403)
            {
                // Passed through, a rejected subscription credential would make the client ask for its own login.
                logger.LogWarning("Claude rejected the token count with HTTP {status}; answering the local estimate", status);
                return await EstimateAsync(call);
            }
            var now = time.GetUtcNow();
            var limits = LimitHeaders.Anthropic(response.Headers);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var head = message && status is 400 or 401 or 403 or 404 ? await ReadHeadAsync(stream, ct) : [];
            if (message && Fault(response, limits, now, head) is { } fault)
            {
                logger.LogWarning("Claude answered HTTP {status}; not serving this request from it ({reason})", status, fault.Kind);
                return UpstreamResult.NotServed(fault);
            }
            await CopyResponseAsync(response, head, stream, context, ct);
            return UpstreamResult.ServedWith(limits.Exhausted ? new ModelFault(FaultKind.Limited, limits.ResetAt) : null);
        }
    }

    /// <summary>A token count is advisory, so the local estimate stands in rather than failing the client.</summary>
    private static async Task<UpstreamResult> EstimateAsync(UpstreamCall call)
    {
        await call.Context.Response.WriteAsJsonAsync(new { input_tokens = call.EstimatedInputTokens }, call.Context.RequestAborted);
        return UpstreamResult.Served;
    }

    /// <summary>Sends once with a valid access token (refreshed first when <paramref name="rejected"/> is the current one).</summary>
    private async Task<(HttpResponseMessage Response, string AccessToken)> SendOnceAsync(HttpContext context, string path, byte[] body, string? rejected)
    {
        var ct = context.RequestAborted;
        var accessToken = await tokens.GetAccessTokenAsync(ct, rejected);
        using var request = BuildRequest(context.Request, path, body, accessToken);
        var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return (response, accessToken);
    }

    /// <summary>
    /// A status another model may do better on: a prompt too long for this model's context (400), a limit (429), a model the plan does not offer (404
    /// <c>not_found_error</c>, 403 <c>permission_error</c> naming the model or plan), a rejected account or credential
    /// (401, any other 403) or an overloaded or failing service.
    /// </summary>
    private static ModelFault? Fault(HttpResponseMessage response, LimitReading limits, DateTimeOffset now, byte[] errorBody)
    {
        var status = (int)response.StatusCode;
        if (status is 400 or 401 or 403 or 404)
        {
            var (type, text) = Error(errorBody);
            return status switch
            {
                400 => ContextOverflow.IsReported(text) ? new ModelFault(FaultKind.ContextOverflow, Message: text) : null,
                404 => type == "not_found_error" ? new ModelFault(FaultKind.PlanRejected) : null,
                403 when type == "permission_error" && text is not null && PlanRefusal.IsMatch(text) => new ModelFault(FaultKind.PlanRejected),
                _ => new ModelFault(FaultKind.AuthRejected),
            };
        }
        return status switch
        {
            429 => new ModelFault(FaultKind.Limited, (limits.Exhausted ? limits.ResetAt : null) ?? LimitHeaders.RetryAfter(response.Headers, now)),
            500 or 502 or 503 or 504 or 529 => new ModelFault(FaultKind.Overloaded, LimitHeaders.RetryAfter(response.Headers, now)),
            _ => null,
        };
    }

    /// <summary>Reads at most one byte past the error limit, so an oversized or endless body is never buffered whole.</summary>
    private static async Task<byte[]> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxErrorBodyBytes + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        return buffer[..length];
    }

    private static (string? Type, string? Message) Error(byte[] body)
    {
        if (body.Length > MaxErrorBodyBytes)
        {
            return (null, null);
        }
        try
        {
            var error = JsonNode.Parse(body) is JsonObject root ? root["error"] as JsonObject : null;
            return (Str(error?["type"]), Str(error?["message"]));
        }
        catch (JsonException)
        {
            return (null, null);
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

    /// <param name="head">Bytes already read from <paramref name="stream"/>, sent first.</param>
    private static async Task CopyResponseAsync(HttpResponseMessage upstream, byte[] head, Stream stream, HttpContext context, CancellationToken ct)
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

        if (head.Length > 0)
        {
            await response.Body.WriteAsync(head, ct);
            await response.Body.FlushAsync(ct);
        }
        var buffer = new byte[CopyBufferBytes];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            await response.Body.FlushAsync(ct);
        }
    }
}
