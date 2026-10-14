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

        tier = (requestedModel is null ? null : AliasTier(requestedModel, rules)) ?? rules.DefaultTier;
        return true;
    }

    /// <summary>The tier of the first alias matching <paramref name="model"/>; null when none does.</summary>
    public static ModelTier? AliasTier(string model, TierRules rules)
    {
        foreach (var (pattern, aliasTier) in rules.Aliases)
        {
            if (GlobMatches(pattern, model))
            {
                return aliasTier;
            }
        }
        return null;
    }

    /// <summary>Bounds a backtracking glob on a client-supplied model name; a timed-out alias does not match.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private static bool GlobMatches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        try
        {
            return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
