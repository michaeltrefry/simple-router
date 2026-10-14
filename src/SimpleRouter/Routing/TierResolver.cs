using System.Text.RegularExpressions;

namespace SimpleRouter.Routing;

/// <summary>Editable routing rules: requested-model aliases (in priority order) and the fallback tier.</summary>
public sealed record TierRules(IReadOnlyList<(string Pattern, ModelTier Tier)> Aliases, ModelTier DefaultTier);

/// <summary>Resolves a request's tier: explicit header, else the requested model's alias, else the default tier.</summary>
public static class TierResolver
{
    public const string HeaderName = "x-weave-model-class";

    /// <returns><c>false</c> when the header is present but names no tier.</returns>
    public static bool TryResolve(string? header, string? requestedModel, TierRules rules, out ModelTier tier)
    {
        if (!string.IsNullOrWhiteSpace(header))
        {
            return ModelTiers.TryParse(header, out tier);
        }

        tier = rules.DefaultTier;
        if (requestedModel is null)
        {
            return true;
        }
        foreach (var (pattern, aliasTier) in rules.Aliases)
        {
            if (GlobMatches(pattern, requestedModel))
            {
                tier = aliasTier;
                break;
            }
        }
        return true;
    }

    private static bool GlobMatches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
