using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SimpleRouter.Routing;

namespace SimpleRouter.Claude;

/// <summary><c>GET /v1/models</c> with the subscription token and the same auth headers as the messages call.</summary>
public sealed class ClaudeModelList(IHttpClientFactory httpClientFactory, ClaudeTokenProvider tokens, IOptions<RouterOptions> options) : IModelListSource
{
    private const int MaxPages = 20;

    public ProviderKind Provider => ProviderKind.Claude;

    public async Task<IReadOnlyList<string>> FetchAsync(CancellationToken ct)
    {
        var accessToken = await tokens.GetAccessTokenAsync(ct);
        var client = ModelListHttp.Client(httpClientFactory, ClaudeUpstream.HttpClientName);
        var ids = new List<string>();
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = options.Value.Claude.ApiBaseUrl.TrimEnd('/') + "/v1/models?limit=1000" + (afterId is null ? "" : "&after_id=" + Uri.EscapeDataString(afterId));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("anthropic-version", ClaudeUpstream.DefaultAnthropicVersion);
            request.Headers.TryAddWithoutValidation("anthropic-beta", ClaudeUpstream.OAuthBeta);

            var body = await ModelListHttp.GetJsonAsync(client, request, "Claude", ct);
            if (body["data"] is not JsonArray data)
            {
                throw new ModelListException("Claude sent a malformed model list.");
            }
            ids.AddRange(data.Select(m => m?["id"]?.GetValue<string>()).OfType<string>());
            var lastId = body["last_id"]?.GetValue<string>();
            if (body["has_more"]?.GetValue<bool>() != true || string.IsNullOrEmpty(lastId) || lastId == afterId)
            {
                break;
            }
            afterId = lastId;
        }
        return ids;
    }
}
