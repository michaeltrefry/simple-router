namespace SimpleRouter.Usage;

/// <summary>Final response metadata only; never carries response content or exception messages.</summary>
public sealed record RequestCompletion(bool IsStreaming, string Outcome, string? FailureKind = null,
    string? FailureDetail = null, string? StopReason = null)
{
    public static RequestCompletion Failed(bool streaming, string kind, string? stopReason = null) =>
        new(streaming, kind == "client_cancelled" ? "client_cancelled" : "failed", kind,
            RequestDiagnostics.Explanation(kind), stopReason);
}

/// <summary>Preserves a provider's safe failure category across translation; reset before each attempt.</summary>
public static class RequestDiagnostics
{
    private static readonly object Key = new();
    public static string? Get(HttpContext context) => context.Items.TryGetValue(Key, out var value) ? value as string : null;
    public static void Reset(HttpContext context) => context.Items.Remove(Key);
    public static void Fail(HttpContext context, string kind) => context.Items[Key] = Explanation(kind) is null ? "upstream_error" : kind;

    public static string? Explanation(string kind) => kind switch
    {
        "upstream_timeout" => "The upstream did not respond before the timeout.",
        "upstream_unreachable" => "The upstream could not be reached.",
        "truncated_stream" => "The stream ended without a message_stop completion event.",
        "malformed_response" => "The upstream response could not be read as a valid message.",
        "upstream_error" => "The upstream reported an error. Upstream error text is not retained.",
        "http_error" => "The request ended with an unsuccessful HTTP status.",
        "client_cancelled" => "The client disconnected or cancelled the request before it completed.",
        "transport_error" => "The response was interrupted by a transport failure.",
        "tier_exhausted" => "No eligible model in this tier could serve the request.",
        "context_overflow" => "The request exceeded the available models' context windows.",
        _ => null,
    };
}
