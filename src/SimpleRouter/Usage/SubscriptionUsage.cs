using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.Usage;

/// <summary>The <c>GET /v1/subscriptions/usage</c> body, in the shape the Dark Factory orchestrator reads.</summary>
public sealed record SubscriptionUsageReport(
    [property: JsonPropertyName("as_of")] DateTimeOffset AsOf,
    [property: JsonPropertyName("all_exhausted")] bool AllExhausted,
    [property: JsonPropertyName("resumes_at")] DateTimeOffset? ResumesAt,
    [property: JsonPropertyName("known_credentials")] int KnownCredentials,
    [property: JsonPropertyName("credentials")] IReadOnlyList<SubscriptionCredential> Credentials);

/// <summary>
/// One stored subscription login. <c>managed</c> marks the router's own login, which the orchestrator counts as enrolled.
/// <c>routable</c> is false only for a login that needs a human to log in again (<c>auth_rejected</c>): a <c>cooling</c>
/// login is spent until its reset, not broken, and an <c>unknown</c> one (no enabled, tiered model of its provider) has
/// nothing a fresh login would fix.
/// </summary>
public sealed record SubscriptionCredential(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("routable")] bool Routable,
    [property: JsonPropertyName("state")] string State);

/// <summary>
/// Reports whether the subscriptions are spent. Only enabled subscription models that belong to a tier count (local
/// models never do): they are exhausted when every one is cooling or auth-rejected, and at least one login is stored.
/// </summary>
public sealed class SubscriptionUsage(IDbContextFactory<RouterDbContext> dbFactory, ModelSelector selector, TimeProvider time)
{
    public const string ManagedSource = "managed";

    public async Task<SubscriptionUsageReport> ReportAsync(CancellationToken ct)
    {
        List<ProviderKind> loggedIn = [];
        HashSet<string> tiered;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.ClaudeCredentials.AnyAsync(ct))
            {
                loggedIn.Add(ProviderKind.Claude);
            }
            if (await db.CodexCredentials.AnyAsync(ct))
            {
                loggedIn.Add(ProviderKind.Codex);
            }
            tiered = (await db.TierMembers.Select(t => t.ModelId).Distinct().ToListAsync(ct)).ToHashSet();
        }

        var counted = (await selector.StatusesAsync(ct))
            .Where(s => loggedIn.Contains(s.Provider) && tiered.Contains(s.ModelId)
                && s.Status is ModelStatus.Eligible or ModelStatus.Cooling or ModelStatus.AuthRejected)
            .ToList();
        var allExhausted = counted.Count > 0 && counted.All(s => s.Status != ModelStatus.Eligible);
        var resumesAt = allExhausted ? counted.Min(s => s.CoolingUntil) : null;

        var credentials = loggedIn.Select(p =>
        {
            var models = counted.Where(s => s.Provider == p).ToList();
            var state = models.Any(s => s.Status == ModelStatus.Eligible) ? "ok"
                : models.Any(s => s.Status == ModelStatus.AuthRejected) ? "auth_rejected"
                : models.Count > 0 ? "cooling"
                : "unknown";
            return new SubscriptionCredential(p.ToString().ToLowerInvariant(), ManagedSource, true, state != "auth_rejected", state);
        }).ToList();

        return new SubscriptionUsageReport(time.GetUtcNow(), allExhausted, resumesAt, loggedIn.Count, credentials);
    }
}
