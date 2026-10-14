namespace SimpleRouter.Tests.Support;

/// <summary>
/// Copies of the strings the Dark Factory orchestrator matches to pause on usage rather than fail work: the reviewer
/// checks the status and the router's class marker, workers (Claude Code) match error text. Kept verbatim so the
/// exhaustion response is checked against what the client actually looks for.
/// </summary>
public static class DarkFactoryMatchers
{
    public const string ModelClassUnavailable = "model_class_unavailable";

    public static readonly string[] UsageLimitMarkers =
    [
        "All enrolled subscription accounts are currently unavailable",
        "API Error: 429",
        "API Error: 529",
        "rate_limit_error",
        "overloaded_error",
        "Repeated 529",
        "usage limit reached",
        "hit your limit",
    ];

    /// <summary>The reviewer's test for a usage refusal.</summary>
    public static bool ReviewerUsageLimited(int status, string body) =>
        status is 429 or 529 || body.Contains(ModelClassUnavailable, StringComparison.OrdinalIgnoreCase)
        || UsageLimitMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>A worker's test on the error text Claude Code reports.</summary>
    public static bool WorkerUsageLimited(string text) =>
        UsageLimitMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>A worker's test for the router's "no model of the class can serve" refusal.</summary>
    public static bool WorkerModelClassUnavailable(string text) =>
        text.Contains(ModelClassUnavailable, StringComparison.OrdinalIgnoreCase);
}
