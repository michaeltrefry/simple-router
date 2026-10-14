using System.Text.Json.Serialization;

namespace SimpleRouter.Tests.Support;

// Copies of the Dark Factory orchestrator's router records (Router/RouterClient.cs), kept verbatim in shape so the
// endpoints are checked against what the client actually deserializes.

public sealed record SessionCost(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("request_count")] long RequestCount,
    [property: JsonPropertyName("actual_cost_usd_micros")] long ActualCostUsdMicros);

public sealed record SubscriptionUsage(
    [property: JsonPropertyName("as_of")] DateTimeOffset AsOf,
    [property: JsonPropertyName("all_exhausted")] bool AllExhausted,
    [property: JsonPropertyName("resumes_at")] DateTimeOffset? ResumesAt,
    [property: JsonPropertyName("known_credentials")] int KnownCredentials,
    [property: JsonPropertyName("credentials")] IReadOnlyList<UsageCredential>? Credentials = null)
{
    public int EnrolledCredentials =>
        Enrolled.Count(c => c.Enabled != false && c.Routable);

    public IEnumerable<UsageCredential> UnroutableEnrolledCredentials =>
        Enrolled.Where(c => c.Enabled != false && !c.Routable);

    private IEnumerable<UsageCredential> Enrolled =>
        Credentials?.Where(c => c.Source is "managed" or "shared") ?? [];
}

public sealed record UsageCredential(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("routable")] bool Routable = false,
    [property: JsonPropertyName("state")] string? State = null);
