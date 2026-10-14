namespace SimpleRouter.Routing;

/// <summary>Fetches the model IDs a subscription currently serves.</summary>
public interface IModelListSource
{
    ProviderKind Provider { get; }

    /// <summary>Throws on any failure; the caller keeps the last good list.</summary>
    Task<IReadOnlyList<string>> FetchAsync(CancellationToken ct);
}

/// <summary>A model list response the router could not use.</summary>
public sealed class ModelListException(string message) : Exception(message);
