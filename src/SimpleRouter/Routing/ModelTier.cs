namespace SimpleRouter.Routing;

/// <summary>Service tier a request is routed in; its lowercase name is the wire and storage form.</summary>
public enum ModelTier
{
    Max,
    High,
    Mid,
    Low,
}

public static class ModelTiers
{
    public static string Name(this ModelTier tier) => tier.ToString().ToLowerInvariant();

    /// <summary>Accepts only the four tier names, case-insensitively (unlike <c>Enum.TryParse</c>, which accepts numbers).</summary>
    public static bool TryParse(string? value, out ModelTier tier)
    {
        foreach (var candidate in Enum.GetValues<ModelTier>())
        {
            if (string.Equals(value?.Trim(), candidate.Name(), StringComparison.OrdinalIgnoreCase))
            {
                tier = candidate;
                return true;
            }
        }
        tier = default;
        return false;
    }

    public static ModelTier Parse(string value) =>
        TryParse(value, out var tier) ? tier : throw new FormatException($"'{value}' is not a model tier.");
}
