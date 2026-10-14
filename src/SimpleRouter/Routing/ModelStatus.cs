namespace SimpleRouter.Routing;

/// <summary>Why a catalog model can or cannot be selected, before per-request needs (context, tools, vision).</summary>
public enum ModelStatus
{
    Eligible,
    Disabled,
    NotLoggedIn,
    UnknownModel,
    Unhealthy,
}

public static class ModelStatuses
{
    public static string Label(this ModelStatus status) => status switch
    {
        ModelStatus.Eligible => "eligible",
        ModelStatus.Disabled => "disabled",
        ModelStatus.NotLoggedIn => "not logged in",
        ModelStatus.UnknownModel => "unknown model",
        _ => "unhealthy",
    };
}

/// <summary>A catalog model and its current <see cref="ModelStatus"/>.</summary>
public sealed record ModelStatusEntry(string ModelId, ProviderKind Provider, ModelStatus Status)
{
    public string Label => Status.Label();
}
