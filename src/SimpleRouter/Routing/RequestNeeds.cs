using System.Text.Json.Nodes;
using SimpleRouter.Data;

namespace SimpleRouter.Routing;

/// <summary>What a Messages request needs from a model to be served: room for its prompt and some output, and tool support.</summary>
public sealed record RequestNeeds(int EstimatedTokens, int MaxTokens, bool UsesTools)
{
    /// <summary>A model must leave at least this much output (or the request's whole <c>max_tokens</c>, if smaller) after the prompt.</summary>
    public const int MinOutputTokens = 1024;

    public static RequestNeeds Of(JsonObject request) => new(
        TokenEstimator.Estimate(request),
        request["max_tokens"] is JsonValue v && v.TryGetValue<int>(out var max) ? Math.Max(0, max) : 0,
        request["tools"] is JsonArray { Count: > 0 });

    public bool CanUse(CatalogModel model) =>
        (model.ContextWindow is not { } window || (long)EstimatedTokens + Math.Min(MaxTokens, MinOutputTokens) <= window)
        && (model.SupportsTools || !UsesTools);
}
