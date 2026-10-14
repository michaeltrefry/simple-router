namespace SimpleRouter.Routing;

/// <summary>Why a catalog model can or cannot be selected, before per-request needs (context, tools, vision).</summary>
public enum ModelStatus
{
    Eligible,
    Disabled,
    NotLoggedIn,
    UnknownModel,
    Unhealthy,
    Cooling,
}

public static class ModelStatuses
{
    public static string Label(this ModelStatus status) => status switch
    {
        ModelStatus.Eligible => "eligible",
        ModelStatus.Disabled => "disabled",
        ModelStatus.NotLoggedIn => "not logged in",
        ModelStatus.UnknownModel => "unknown model",
        ModelStatus.Cooling => "cooling",
        _ => "unhealthy",
    };
}

/// <summary>A catalog model, its current <see cref="ModelStatus"/> and, while cooling, when it may be chosen again.</summary>
public sealed record ModelStatusEntry(string ModelId, ProviderKind Provider, ModelStatus Status, DateTimeOffset? CoolingUntil = null)
{
    public string Label => Status == ModelStatus.Cooling && CoolingUntil is { } until
        ? $"cooling until {until.UtcDateTime:u}"
        : Status.Label();
}
