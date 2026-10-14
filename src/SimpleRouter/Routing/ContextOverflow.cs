using System.Text.RegularExpressions;

namespace SimpleRouter.Routing;

/// <summary>Recognizes a provider's "the request does not fit the context window" rejection by its message.</summary>
public static class ContextOverflow
{
    // Token estimates are approximate, so a provider's own context rejection still moves the request to another model.
    private static readonly Regex Message = new(
        @"context[ _-]?(length|window|size)|maximum context|too many tokens|prompt (is )?too long|context (the )?overflow|exceeds? the (model'?s )?(max|context)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsReported(string? message) => message is not null && Message.IsMatch(message);
}
