namespace SimpleRouter.Routing;

/// <summary>Which upstream serves a catalog model.</summary>
public enum ProviderKind
{
    Claude,
    Codex,
    Local,

    /// <summary>A SystemOne decision model, served only at <c>/v1/systemone</c>; never a tier member.</summary>
    SystemOne,

    /// <summary>OpenRouter API-key provider, including auto-routed variants.</summary>
    OpenRouter,
}
