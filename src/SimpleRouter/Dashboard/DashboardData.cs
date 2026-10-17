using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Usage;

namespace SimpleRouter.Dashboard;

/// <summary>One served request as the dashboard lists it.</summary>
public sealed record RequestRow(
    DateTimeOffset At, string? ClientSessionId, ModelTier? Tier, string ModelId, ProviderKind Provider, string? Effort,
    long LatencyMs, int Status, long InputTokens, long OutputTokens, long CostUsdMicros);

/// <summary>A session pin and the current status of its pinned model.</summary>
public sealed record PinRow(string SessionKey, string? ClientSessionId, ModelTier Tier, string ModelId, DateTimeOffset CreatedAt, ModelStatusEntry? Status);

/// <summary>A subscription login and its model list fetch state; never carries tokens or account identifiers.</summary>
public sealed record AccountRow(
    ProviderKind Provider, bool LoggedIn, string State, DateTimeOffset? ListFetchedAt, DateTimeOffset? ListCheckedAt, int? ListedModels, string? ListError);

/// <summary>The window the usage charts cover: the last 24 hours by hour, or the last 7 or 30 days by day (UTC).</summary>
public enum UsageRange { Day, Week, Month }

/// <summary>One model's requests and cost per chart bucket.</summary>
public sealed record UsageSeries(string ModelId, long[] Requests, long[] CostUsdMicros);

/// <summary>Recorded usage in a <see cref="UsageRange"/>: bucket starts, one series per model (by model ID), and totals.</summary>
public sealed record UsageChart(
    UsageRange Range, DateTimeOffset[] Buckets, List<UsageSeries> Series, long Requests, long Tokens, long CostUsdMicros);

/// <summary>Read-only queries behind the monitoring dashboard.</summary>
public sealed class DashboardData(
    IDbContextFactory<RouterDbContext> dbFactory, ModelSelector selector, SubscriptionUsage subscriptions, TimeProvider time)
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

    public async Task<UsageChart> UsageChartAsync(UsageRange range, CancellationToken ct)
    {
        var (step, count) = range switch
        {
            UsageRange.Day => (TimeSpan.FromHours(1), 24),
            UsageRange.Week => (TimeSpan.FromDays(1), 7),
            _ => (TimeSpan.FromDays(1), 30),
        };
        var now = time.GetUtcNow();
        var end = new DateTimeOffset(now.Ticks - now.Ticks % step.Ticks, TimeSpan.Zero) + step;
        var start = end - step * count;
        var series = new SortedDictionary<string, UsageSeries>(StringComparer.Ordinal);
        long requests = 0, tokens = 0, cost = 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // SQLite cannot compare a DateTimeOffset column; rows are written in time order, so read newest first and stop at the window's start.
        var rows = db.RequestUsages.AsNoTracking()
            .OrderByDescending(u => u.Id)
            .Select(u => new { u.At, u.ModelId, Tokens = u.InputTokens + u.OutputTokens, u.CostUsdMicros })
            .AsAsyncEnumerable();
        await foreach (var u in rows.WithCancellation(ct))
        {
            if (u.At < start)
            {
                break;
            }
            if (u.At >= end)
            {
                continue;
            }
            if (!series.TryGetValue(u.ModelId, out var s))
            {
                series[u.ModelId] = s = new UsageSeries(u.ModelId, new long[count], new long[count]);
            }
            var bucket = (int)((u.At - start) / step);
            s.Requests[bucket]++;
            s.CostUsdMicros[bucket] += u.CostUsdMicros;
            requests++;
            tokens += u.Tokens;
            cost += u.CostUsdMicros;
        }
        var buckets = Enumerable.Range(0, count).Select(i => start + step * i).ToArray();
        return new UsageChart(range, buckets, [.. series.Values], requests, tokens, cost);
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
