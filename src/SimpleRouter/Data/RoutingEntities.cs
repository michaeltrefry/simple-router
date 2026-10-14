using SimpleRouter.Routing;

namespace SimpleRouter.Data;

/// <summary>A routable model; <see cref="UpstreamModelId"/> is what its provider is sent in the body's <c>model</c>.</summary>
public sealed class CatalogModel
{
    public required string Id { get; set; }
    public ProviderKind Provider { get; set; }
    public required string UpstreamModelId { get; set; }
    public bool Enabled { get; set; }

    /// <summary>OpenAI-compatible base URL including its version segment (e.g. <c>http://localhost:8000/v1</c>); local models only.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Optional bearer key for <see cref="BaseUrl"/>, protected with <see cref="SecretProtector"/>.</summary>
    public string? ProtectedApiKey { get; set; }

    /// <summary>Maximum estimated input tokens the model accepts; null means unlimited.</summary>
    public int? ContextWindow { get; set; }

    public bool SupportsTools { get; set; } = true;
    public bool SupportsVision { get; set; }
}

/// <summary>Membership of a model in a tier; a model may belong to several tiers.</summary>
public sealed class TierMember
{
    public ModelTier Tier { get; set; }
    public required string ModelId { get; set; }
    public int Position { get; set; }
}

/// <summary>Maps a requested model name glob (<c>*</c>, <c>?</c>) to a tier; lower <see cref="Position"/> wins.</summary>
public sealed class TierAlias
{
    public required string Pattern { get; set; }
    public ModelTier Tier { get; set; }
    public int Position { get; set; }
}

/// <summary>Persisted round-robin position for new sessions in a tier.</summary>
public sealed class TierCursor
{
    public ModelTier Tier { get; set; }
    public long Next { get; set; }
}

/// <summary>The model a session is pinned to within one tier.</summary>
public sealed class SessionPin
{
    public required string SessionKey { get; set; }
    public ModelTier Tier { get; set; }
    public required string ModelId { get; set; }
    public string? ClientSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
