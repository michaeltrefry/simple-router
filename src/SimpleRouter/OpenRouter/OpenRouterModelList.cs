using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SimpleRouter.Routing;

namespace SimpleRouter.OpenRouter;

/// <summary>The catalog, authenticated with the same shared key used for inference.</summary>
public sealed class OpenRouterModelList(
    IHttpClientFactory httpClientFactory,
    OpenRouterCredentialStore credentials,
    IOptions<RouterOptions> options) : IModelListSource
{
    public ProviderKind Provider => ProviderKind.OpenRouter;

    public async Task<IReadOnlyList<string>> FetchAsync(CancellationToken ct) =>
        (await FetchModelsAsync(ct)).Select(m => m.Id).ToList();

    public async Task<IReadOnlyList<OpenRouterModelInfo>> FetchModelsAsync(CancellationToken ct)
    {
        try
        {
            var key = await credentials.GetAsync(ct);
            if (string.IsNullOrWhiteSpace(key))
                throw new ModelListException("OpenRouter has no API key configured.");
            var client = ModelListHttp.Client(httpClientFactory, OpenRouterUpstream.HttpClientName);
            var request = new HttpRequestMessage(HttpMethod.Get, options.Value.OpenRouter.ApiBaseUrl.TrimEnd('/') + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var body = await ModelListHttp.GetJsonAsync(client, request, "OpenRouter", ct);
            if (body["data"] is not JsonArray data)
                throw new ModelListException("OpenRouter sent a malformed model list.");
            return data.Select(Parse).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (ModelListHttpException) { throw; }
        catch (ModelListException) { throw; }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Transport, JSON and credential failures can contain URLs, response fragments or secrets.
            throw new ModelListException("OpenRouter model list could not be read.");
        }
    }

    public static bool IsAuto(string id) => OpenRouterRoutingOptions.IsAuto(id);

    private static OpenRouterModelInfo Parse(JsonNode? node)
    {
        if (node is not JsonObject model || model["id"]?.GetValue<string>() is not { Length: > 0 } id)
            throw new ModelListException("OpenRouter sent a malformed model list.");
        if (IsAuto(id))
            return new(id, null, null, null, null, null, null, null, null, null);
        var context = model["context_length"]?.GetValue<int>();
        if (context <= 0)
            throw new ModelListException("OpenRouter sent an invalid context length.");
        var parameters = model["supported_parameters"] as JsonArray;
        var modalities = model["architecture"]?["input_modalities"] as JsonArray;
        return new(id, context,
            parameters is null ? null : parameters.Any(p => p?.GetValue<string>() == "tools"),
            modalities is null ? null : modalities.Any(p => p?.GetValue<string>() == "image"),
            Price(model["pricing"]?["prompt"]), Price(model["pricing"]?["completion"]),
            Price(model["pricing"]?["input_cache_read"]), Price(model["pricing"]?["input_cache_write"]), Price(model["pricing"]?["input_cache_write_1h"]), Reasoning(model["reasoning"]));
    }

    private static EffortMapping? Reasoning(JsonNode? node)
    {
        if (node?["supported_efforts"] is not JsonArray efforts) return null;
        var levels = efforts.Select(e => e?.GetValue<string>())
            .Where(e => Effort.TryParse(e, out _)).Select(e => { Effort.TryParse(e, out var level); return level; })
            .Distinct().Order().ToArray();
        if (levels.Length == 0) return null;
        EffortLevel? defaultEffort = node["default_enabled"]?.GetValue<bool>() != false
            && Effort.TryParse(node["default_effort"]?.GetValue<string>(), out var value) && levels.Contains(value) ? value : null;
        return new EffortMapping(EffortMode.ReasoningEffort, levels, Default: defaultEffort);
    }

    private static decimal? Price(JsonNode? value)
    {
        if (value is null) return null;
        if (!decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price) || price < -1)
            throw new ModelListException("OpenRouter sent invalid model pricing.");
        // Dynamic routers advertise -1 rather than a fixed price. Unknown is not a free model.
        return price < 0 ? null : checked(price * 1_000_000m);
    }
}

public sealed record OpenRouterModelInfo(string Id, int? ContextWindow, bool? SupportsTools, bool? SupportsVision,
    decimal? InputPerMTok, decimal? OutputPerMTok, decimal? CacheReadPerMTok, decimal? CacheWritePerMTok, decimal? CacheWrite1hPerMTok, EffortMapping? Effort);
