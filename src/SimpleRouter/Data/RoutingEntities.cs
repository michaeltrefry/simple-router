using SimpleRouter.Routing;
using SimpleRouter.OpenRouter;

namespace SimpleRouter.Data;

/// <summary>A routable model; <see cref="UpstreamModelId"/> is what its provider is sent in the body's <c>model</c>.</summary>
public sealed class CatalogModel
{
    public required string Id { get; set; }
    public ProviderKind Provider { get; set; }
    public required string UpstreamModelId { get; set; }
    public bool Enabled { get; set; }

    /// <summary>OpenAI-compatible base URL including its version segment (e.g. <c>http://localhost:8000/v1</c>); local and SystemOne models only.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Optional bearer key for <see cref="BaseUrl"/>, protected with <see cref="SecretProtector"/>.</summary>
    public string? ProtectedApiKey { get; set; }

    /// <summary>Total tokens (prompt plus output) the model accepts; null means unlimited.</summary>
    public int? ContextWindow { get; set; }

    /// <summary>
    /// The largest estimated prompt this model is given; a larger one goes to the tier's next model. Unlike
    /// <see cref="ContextWindow"/> it leaves <c>max_tokens</c> alone, so a slow local model can shed long prompts. Null means no cap.
    /// </summary>
    public int? MaxPromptTokens { get; set; }

    /// <summary>
    /// How many conversations a local server keeps cached at once (a single-slot server: 1); while that many others are
    /// active a new one goes to the tier's next model (<see cref="LocalConversations"/>). Null means no limit.
    /// </summary>
    public int? MaxConversations { get; set; }

    public bool SupportsTools { get; set; } = true;
    public bool SupportsVision { get; set; }

    /// <summary>Supported reasoning-effort levels and how they are sent; null means the provider's defaults.</summary>
    public EffortMapping? Effort { get; set; }

    /// <summary>Auto-routing constraints, independent of reasoning effort.</summary>
    public OpenRouterRoutingOptions? OpenRouter { get; set; }
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

/// <summary>A subscription's discovered model list; <see cref="ModelIds"/> stays null until a fetch has succeeded.</summary>
public sealed class ProviderModelList
{
    public ProviderKind Provider { get; set; }
    public List<string>? ModelIds { get; set; }

    /// <summary>When <see cref="ModelIds"/> was last fetched successfully.</summary>
    public DateTimeOffset? FetchedAt { get; set; }

    /// <summary>When a fetch was last attempted, successful or not.</summary>
    public DateTimeOffset CheckedAt { get; set; }

    /// <summary>Why the last attempt failed; null when it succeeded.</summary>
    public string? LastError { get; set; }
}

/// <summary>A model that must not be chosen until <see cref="Until"/>: a usage limit, plan rejection or overload.</summary>
public sealed class ModelCooldown
{
    public required string ModelId { get; set; }
    public DateTimeOffset Until { get; set; }
    public FaultKind Reason { get; set; }
}

/// <summary>A provider-level API key encrypted with SecretProtector.</summary>
public sealed class ProviderCredential
{
    public ProviderKind Provider { get; set; }
    public required string ProtectedApiKey { get; set; }
}
