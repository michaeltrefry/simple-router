using System.Text.Json.Nodes;
using SimpleRouter.Data;

namespace SimpleRouter.Routing;

/// <summary>A routed Messages API call: the parsed request, its session, the selected model and the token estimate used to select it.</summary>
public sealed record UpstreamCall(HttpContext Context, JsonObject Request, SessionIdentity Session, CatalogModel Model, bool CountTokens, int EstimatedInputTokens);

/// <summary>Serves Messages API calls for every catalog model of one <see cref="ProviderKind"/>, writing an Anthropic-shaped response.</summary>
public interface IModelUpstream
{
    ProviderKind Provider { get; }

    Task SendAsync(UpstreamCall call);
}
