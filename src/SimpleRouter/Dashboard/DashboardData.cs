using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Usage;

namespace SimpleRouter.Dashboard;

/// <summary>One served request as the dashboard lists it.</summary>
public sealed record RequestRow(
    DateTimeOffset At, string? ClientSessionId, ModelTier? Tier, string ModelId, ProviderKind Provider, string? Effort,
    long LatencyMs, int Status, long InputTokens, long OutputTokens, long CostUsdMicros,
    string? ResolvedModelId = null, string? GenerationId = null, string? CostSource = null,
    long Id = 0, string? SessionKey = null, string? UpstreamModelId = null, bool? IsStreaming = null,
    string? Outcome = null, string? FailureKind = null, string? FailureDetail = null, string? StopReason = null,
    long CacheReadTokens = 0, long CacheWriteTokens = 0, long CacheWrite1hTokens = 0);

public sealed record RequestPage(List<RequestRow> Rows, long? NextBeforeId);

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
    UsageRange Range, DateTimeOffset[] Buckets, List<UsageSeries> Series, long Requests, long Tokens, long CostUsdMicros, long UnknownCostRequests = 0);

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
            .Select(RequestProjection)
            .ToListAsync(ct);
    }

    private static readonly Expression<Func<RequestUsage, RequestRow>> RequestProjection = u => new RequestRow(
        u.At, u.ClientSessionId, u.Tier, u.ModelId, u.Provider, u.Effort, u.LatencyMs, u.Status,
        u.InputTokens, u.OutputTokens, u.CostUsdMicros, u.ResolvedModelId, u.GenerationId, u.CostSource,
        u.Id, u.SessionKey, u.UpstreamModelId, u.IsStreaming, u.Outcome, u.FailureKind, u.FailureDetail, u.StopReason,
        u.CacheReadTokens, u.CacheWriteTokens, u.CacheWrite1hTokens);

    public async Task<RequestRow?> RequestAsync(long id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RequestUsages.AsNoTracking().Where(u => u.Id == id).Select(RequestProjection).SingleOrDefaultAsync(ct);
    }

    public async Task<RequestPage> PinRequestsAsync(string sessionKey, ModelTier tier, long? beforeId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // The routing key and tier identify a pin across model changes; client IDs and model IDs do not.
        var query = db.RequestUsages.AsNoTracking().Where(u => u.SessionKey == sessionKey && u.Tier == tier);
        if (beforeId is { } cursor) query = query.Where(u => u.Id < cursor);
        var rows = await query.OrderByDescending(u => u.Id).Take(RecentLimit + 1).Select(RequestProjection).ToListAsync(ct);
        var hasMore = rows.Count > RecentLimit;
        if (hasMore) rows.RemoveAt(RecentLimit);
        return new RequestPage(rows, hasMore ? rows[^1].Id : null);
    }

    public async Task<PinRow?> PinAsync(string sessionKey, ModelTier tier, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pin = await db.SessionPins.AsNoTracking().SingleOrDefaultAsync(p => p.SessionKey == sessionKey && p.Tier == tier, ct);
        if (pin is null) return null;
        var status = (await selector.StatusesAsync(ct)).FirstOrDefault(s => s.ModelId == pin.ModelId);
        return new PinRow(pin.SessionKey, pin.ClientSessionId, pin.Tier, pin.ModelId, pin.CreatedAt, status);
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
        long requests = 0, tokens = 0, cost = 0, unknown = 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // SQLite cannot compare a DateTimeOffset column; rows are written in time order, so read newest first and stop at the window's start.
        var rows = db.RequestUsages.AsNoTracking()
            .OrderByDescending(u => u.Id)
            .Select(u => new { u.At, u.ModelId, Tokens = u.InputTokens + u.OutputTokens, u.CostUsdMicros, u.CostSource })
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
            if (u.CostSource == "unknown") unknown++;
        }
        var buckets = Enumerable.Range(0, count).Select(i => start + step * i).ToArray();
        return new UsageChart(range, buckets, [.. series.Values], requests, tokens, cost, unknown);
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
        bool openRouterKey, openRouterConfigured;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            lists = await db.ProviderModelLists.AsNoTracking().ToListAsync(ct);
            openRouterKey = await db.ProviderCredentials.AnyAsync(c => c.Provider == ProviderKind.OpenRouter, ct);
            openRouterConfigured = await db.Models.AnyAsync(m => m.Provider == ProviderKind.OpenRouter, ct);
        }
        var report = await subscriptions.ReportAsync(ct);
        var accounts = new[] { ProviderKind.Claude, ProviderKind.Codex }.Select(provider =>
        {
            var credential = report.Credentials.FirstOrDefault(c => c.Provider == provider.ToString().ToLowerInvariant());
            var list = lists.FirstOrDefault(l => l.Provider == provider);
            return new AccountRow(provider, credential is not null, credential?.State ?? "not_logged_in",
                list?.FetchedAt, list?.CheckedAt, list?.ModelIds?.Count, list?.LastError);
        }).ToList();
        if (openRouterKey || openRouterConfigured)
        {
            var list = lists.FirstOrDefault(l => l.Provider == ProviderKind.OpenRouter);
            var statuses = (await selector.StatusesAsync(ct)).Where(s => s.Provider == ProviderKind.OpenRouter).ToList();
            var state = !openRouterKey ? "not_logged_in"
                : statuses.Any(s => s.Status == ModelStatus.Eligible) ? "ok"
                : statuses.Any(s => s.Status == ModelStatus.AuthRejected) ? "auth_rejected"
                : statuses.Any(s => s.Status == ModelStatus.Cooling) ? "cooling" : "unknown";
            accounts.Add(new AccountRow(ProviderKind.OpenRouter, openRouterKey, state,
                list?.FetchedAt, list?.CheckedAt, list?.ModelIds?.Count, list?.LastError));
        }
        return accounts;
    }
}
