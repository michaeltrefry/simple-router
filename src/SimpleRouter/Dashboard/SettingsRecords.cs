using SimpleRouter.Routing;

namespace SimpleRouter.Dashboard;

/// <summary>A catalog model as the settings page shows it; the API key is only ever reported as set or not.</summary>
public sealed record ModelRow(
    string Id, ProviderKind Provider, string UpstreamModelId, bool Enabled, string? BaseUrl, bool HasApiKey, int? ContextWindow,
    bool SupportsTools, bool SupportsVision, EffortMapping? Effort, IReadOnlyList<ModelTier> Tiers, int? MaxPromptTokens = null);

/// <summary>A model's prices in USD per million tokens; a model without a stored row shows zeros with <see cref="Stored"/> false.</summary>
public sealed record PriceRow(
    string ModelId, decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, decimal CacheWrite1h, bool Stored = true);

/// <summary>A requested-model glob and the tier it selects.</summary>
public sealed record AliasRow(string Pattern, ModelTier Tier);

/// <summary>Everything the settings page edits, read in one go.</summary>
public sealed record SettingsSnapshot(
    IReadOnlyList<ModelRow> Models,
    IReadOnlyDictionary<ModelTier, IReadOnlyList<string>> Tiers,
    IReadOnlyList<AliasRow> Aliases,
    ModelTier DefaultTier,
    IReadOnlyList<PriceRow> Prices,
    IReadOnlyDictionary<ProviderKind, IReadOnlyList<string>> Discovered);

/// <summary>
/// A local model to add or update. <see cref="NewApiKey"/> replaces the stored key when set; <see cref="ClearApiKey"/>
/// removes it; neither keeps it.
/// </summary>
public sealed record LocalModelInput(
    string Id, string UpstreamModelId, string BaseUrl, int? ContextWindow, bool SupportsTools, bool SupportsVision, bool Enabled,
    EffortMapping? Effort, IReadOnlyCollection<ModelTier> Tiers, string? NewApiKey = null, bool ClearApiKey = false, int? MaxPromptTokens = null);

/// <summary>A SystemOne decision model to add or update; the API key fields work as in <see cref="LocalModelInput"/>.</summary>
public sealed record SystemOneModelInput(string Id, string UpstreamModelId, string BaseUrl, bool Enabled, string? NewApiKey = null, bool ClearApiKey = false);

/// <summary>A settings edit that was refused; the message is shown to the user.</summary>
public sealed class SettingsException(string message) : Exception(message);
