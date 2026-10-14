using System.Text.Json.Nodes;
using SimpleRouter.Data;

namespace SimpleRouter.Routing;

/// <summary>A routed Messages API call: the parsed request, its session, the selected model and the token estimate used to select it.</summary>
public sealed record UpstreamCall(HttpContext Context, JsonObject Request, SessionIdentity Session, CatalogModel Model, bool CountTokens, int EstimatedInputTokens);

/// <summary>Why a model could not serve, or should not be chosen for a while.</summary>
public enum FaultKind
{
    /// <summary>A usage limit or rate limit (HTTP 429, or limit headers reporting an exhausted window).</summary>
    Limited,

    /// <summary>The provider refused the model for this plan or account.</summary>
    PlanRejected,

    /// <summary>The provider is overloaded or failing (5xx, 529).</summary>
    Overloaded,

    /// <summary>No response arrived: connection refused or failed.</summary>
    Unreachable,

    /// <summary>The model reported the request does not fit its context window.</summary>
    ContextOverflow,
}

/// <summary>A <see cref="FaultKind"/> with the reset the provider reported, if any.</summary>
public sealed record ModelFault(FaultKind Kind, DateTimeOffset? ResetAt = null);

/// <summary>
/// The outcome of one upstream attempt. When <see cref="Written"/> is false nothing reached the client and
/// <see cref="Fault"/> says why, so the router may serve the request on another model. A written result may still carry
/// a fault (a limit reported mid-stream or in a successful response's headers) that cools the model for later requests.
/// </summary>
public sealed record UpstreamResult(bool Written, ModelFault? Fault)
{
    public static readonly UpstreamResult Served = new(true, null);

    public static UpstreamResult ServedWith(ModelFault? fault) => new(true, fault);

    public static UpstreamResult NotServed(ModelFault fault) => new(false, fault);
}

/// <summary>Serves Messages API calls for every catalog model of one <see cref="ProviderKind"/>, writing an Anthropic-shaped response.</summary>
public interface IModelUpstream
{
    ProviderKind Provider { get; }

    /// <summary>
    /// Serves <paramref name="call"/>, or returns <see cref="UpstreamResult.NotServed"/> without touching the response
    /// when the model cannot serve it and another model might. Never returns not-served after writing.
    /// </summary>
    Task<UpstreamResult> SendAsync(UpstreamCall call);
}
