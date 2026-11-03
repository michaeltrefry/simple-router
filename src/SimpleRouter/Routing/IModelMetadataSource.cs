namespace SimpleRouter.Routing;

/// <summary>A model list with capabilities and prices in USD per million tokens.</summary>
public interface IModelMetadataSource : IModelListSource
{
    Task<IReadOnlyList<DiscoveredModel>> FetchModelsAsync(CancellationToken ct);
}

public sealed record DiscoveredModel(string Id, int? ContextWindow, bool? SupportsTools, bool? SupportsVision,
    decimal? InputPerMTok, decimal? OutputPerMTok, decimal? CacheReadPerMTok, decimal? CacheWritePerMTok,
    decimal? CacheWrite1hPerMTok, EffortMapping? Effort);
