using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SimpleRouter.Routing;

namespace SimpleRouter.Codex;

/// <summary><c>GET {ApiBaseUrl}/models</c>: the models this ChatGPT account may use from the configured Codex client version.</summary>
public sealed class CodexModelList(IHttpClientFactory httpClientFactory, CodexTokenProvider tokens, IOptions<RouterOptions> options) : IModelListSource
{
    public ProviderKind Provider => ProviderKind.Codex;

    public async Task<IReadOnlyList<string>> FetchAsync(CancellationToken ct)
    {
        var client = ModelListHttp.Client(httpClientFactory, CodexUpstream.HttpClientName);
        var access = await tokens.GetAccessAsync(ct);
        JsonObject body;
        try
        {
            body = await ModelListHttp.GetJsonAsync(client, Request(access), "Codex", ct);
        }
        catch (ModelListHttpException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            // The access token may have been revoked before its expiry; one forced refresh decides.
            access = await tokens.GetAccessAsync(ct, rejected: access.AccessToken);
            body = await ModelListHttp.GetJsonAsync(client, Request(access), "Codex", ct);
        }
        if (body["models"] is not JsonArray models)
        {
            throw new ModelListException("Codex sent a malformed model list.");
        }
        return models.Select(m => m?["slug"]?.GetValue<string>()).OfType<string>().ToList();
    }

    private HttpRequestMessage Request(CodexAccess access)
    {
        var codex = options.Value.Codex;
        var url = codex.ApiBaseUrl.TrimEnd('/') + "/models?client_version=" + Uri.EscapeDataString(codex.ClientVersion);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", access.AccountId);
        request.Headers.TryAddWithoutValidation("originator", CodexUpstream.Originator);
        request.Headers.TryAddWithoutValidation("User-Agent", CodexUpstream.Originator);
        return request;
    }
}
