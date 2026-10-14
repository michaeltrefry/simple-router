using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Usage;

namespace SimpleRouter.Dashboard;

/// <summary>One served request as the dashboard lists it.</summary>
public sealed record RequestRow(
    DateTimeOffset At, string? ClientSessionId, ModelTier Tier, string ModelId, ProviderKind Provider, string? Effort,
    long LatencyMs, int Status, long InputTokens, long OutputTokens, long CostUsdMicros);

/// <summary>A session pin and the current status of its pinned model.</summary>
public sealed record PinRow(string SessionKey, string? ClientSessionId, ModelTier Tier, string ModelId, DateTimeOffset CreatedAt, ModelStatusEntry? Status);

/// <summary>A subscription login and its model list fetch state; never carries tokens or account identifiers.</summary>
public sealed record AccountRow(
    ProviderKind Provider, bool LoggedIn, string State, DateTimeOffset? ListFetchedAt, DateTimeOffset? ListCheckedAt, int? ListedModels, string? ListError);

/// <summary>Read-only queries behind the monitoring dashboard.</summary>
public sealed class DashboardData(IDbContextFactory<RouterDbContext> dbFactory, ModelSelector selector, SubscriptionUsage subscriptions)
{
    public const int RecentLimit = 50;

    public async Task<List<RequestRow>> RecentRequestsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RequestUsages.AsNoTracking()
            .OrderByDescending(u => u.Id)
            .Take(RecentLimit)
            .Select(u => new RequestRow(u.At, u.ClientSessionId, u.Tier, u.ModelId, u.Provider, u.Effort, u.LatencyMs, u.Status,
                u.InputTokens, u.OutputTokens, u.CostUsdMicros))
            .ToListAsync(ct);
    }

    public async Task<List<PinRow>> PinsAsync(CancellationToken ct)
    {
        List<SessionPin> pins;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // SQLite cannot order by a DateTimeOffset column; a pin's rowid follows its creation, which never changes.
            pins = await db.SessionPins
                .FromSql($"SELECT * FROM \"SessionPins\" ORDER BY rowid DESC LIMIT {RecentLimit}")
                .AsNoTracking()
                .ToListAsync(ct);
        }
        var statuses = (await selector.StatusesAsync(ct)).ToDictionary(s => s.ModelId);
        return pins.OrderByDescending(p => p.CreatedAt)
            .Select(p => new PinRow(p.SessionKey, p.ClientSessionId, p.Tier, p.ModelId, p.CreatedAt, statuses.GetValueOrDefault(p.ModelId)))
            .ToList();
    }

    public Task<List<ModelStatusEntry>> ModelStatusesAsync(CancellationToken ct) => selector.StatusesAsync(ct);

    public async Task<List<AccountRow>> AccountsAsync(CancellationToken ct)
    {
        List<ProviderModelList> lists;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            lists = await db.ProviderModelLists.AsNoTracking().ToListAsync(ct);
        }
        var report = await subscriptions.ReportAsync(ct);
        return new[] { ProviderKind.Claude, ProviderKind.Codex }.Select(provider =>
        {
            var credential = report.Credentials.FirstOrDefault(c => c.Provider == provider.ToString().ToLowerInvariant());
            var list = lists.FirstOrDefault(l => l.Provider == provider);
            return new AccountRow(provider, credential is not null, credential?.State ?? "not_logged_in",
                list?.FetchedAt, list?.CheckedAt, list?.ModelIds?.Count, list?.LastError);
        }).ToList();
    }
}
