using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SimpleRouter.Routing;

namespace SimpleRouter.RouterCom;

/// <summary>Router.com's authenticated catalog; decision-only models do not enter text tiers.</summary>
public sealed class RouterComModelList(
    IHttpClientFactory httpClientFactory, RouterComCredentialStore credentials, IOptions<RouterOptions> options)
    : IModelMetadataSource
{
    public ProviderKind Provider => ProviderKind.RouterCom;

    public async Task<IReadOnlyList<string>> FetchAsync(CancellationToken ct) =>
        (await FetchModelsAsync(ct)).Select(m => m.Id).ToList();

    public async Task<IReadOnlyList<DiscoveredModel>> FetchModelsAsync(CancellationToken ct)
    {
        try
        {
            var key = await credentials.GetAsync(ct);
            if (string.IsNullOrWhiteSpace(key)) throw new ModelListException("Router.com has no API key configured.");
            var client = ModelListHttp.Client(httpClientFactory, RouterComUpstream.HttpClientName);
            var request = new HttpRequestMessage(HttpMethod.Get, options.Value.RouterCom.ApiBaseUrl.TrimEnd('/') + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var body = await ModelListHttp.GetJsonAsync(client, request, "Router.com", ct);
            if (body["data"] is not JsonArray data) throw new ModelListException("Router.com sent a malformed model list.");
            var models = new List<DiscoveredModel>();
            foreach (var node in data)
            {
                if (node is not JsonObject model || model["id"]?.GetValue<string>() is not { Length: > 0 } id)
                    throw new ModelListException("Router.com sent a malformed model list.");
                var router = model["router"];
                if (router?["surfaces"] is JsonArray surfaces && !surfaces.Any(s => s?.GetValue<string>() == "responses")) continue;
                var context = (router?["limits"]?["context_window"] ?? model["context_window"] ?? model["context_length"])?.GetValue<int>();
                if (context <= 0) throw new ModelListException("Router.com sent an invalid context window.");
                var capabilities = router?["capabilities"];
                var inputs = capabilities?["modalities"]?["input"] as JsonArray;
                var pricing = model["router_alias"] is null ? router?["pricing"] : null;
                models.Add(new(id, context, capabilities?["tools"]?["supported"]?.GetValue<bool>(),
                    inputs is null ? null : inputs.Any(s => s?.GetValue<string>() == "image"),
                    Price(pricing?["input"]), Price(pricing?["output"]), Price(pricing?["cache_read_input"]),
                    Price(pricing?["cache_write_input_5m"] ?? pricing?["cache_write_input"]),
                    Price(pricing?["cache_write_input_1h"] ?? pricing?["cache_write_input"]), Reasoning(capabilities?["reasoning"])));
            }
            return models;
        }
        catch (OperationCanceledException) { throw; }
        catch (ModelListHttpException) { throw; }
        catch (ModelListException) { throw; }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            throw new ModelListException("Router.com model list could not be read.");
        }
    }

    private static decimal? Price(JsonNode? value)
    {
        if (value is null) return null;
        if (!decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price) || price < 0)
            throw new ModelListException("Router.com sent invalid model pricing.");
        return price;
    }

    private static EffortMapping? Reasoning(JsonNode? node)
    {
        if (node?["efforts"] is not JsonArray efforts) return null;
        var levels = efforts.Select(e => e?["value"]?.GetValue<string>()).Where(e => Effort.TryParse(e, out _))
            .Select(e => { Effort.TryParse(e, out var level); return level; }).Distinct().Order().ToArray();
        if (levels.Length == 0) return null;
        EffortLevel? defaultEffort = Effort.TryParse(node["default_effort"]?.GetValue<string>(), out var value) && levels.Contains(value) ? value : null;
        return new(EffortMode.ReasoningEffort, levels, Default: defaultEffort);
    }
}
