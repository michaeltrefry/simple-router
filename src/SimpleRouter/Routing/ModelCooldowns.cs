using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleRouter.Data;

namespace SimpleRouter.Routing;

/// <summary>
/// Persists how long a faulted model is skipped. A provider-reported reset wins; otherwise the configured cooldown for
/// the fault applies. Context overflow and an unreachable local endpoint (health handles it) cool nothing.
/// </summary>
public sealed class ModelCooldowns(
    IDbContextFactory<RouterDbContext> dbFactory,
    IOptions<RouterOptions> options,
    TimeProvider time,
    ILogger<ModelCooldowns> logger)
{
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromDays(31);

    /// <summary>Records <paramref name="fault"/> for <paramref name="model"/>; an existing longer cooldown is kept.</summary>
    public async Task RecordAsync(CatalogModel model, ModelFault fault)
    {
        if (Until(model, fault) is not { } until)
        {
            return;
        }
        // Not the request's token: a client that disconnects after a 429 must not erase the limit it revealed.
        try
        {
            await SaveAsync(model.Id, until, fault.Kind);
        }
        catch (DbUpdateException)
        {
            // A concurrent request inserted the row first.
            await SaveAsync(model.Id, until, fault.Kind);
        }
        logger.LogWarning("Model {model} is cooling until {until:u} ({reason})", model.Id, until.UtcDateTime, fault.Kind);
    }

    /// <summary>Models still cooling and when each may be chosen again.</summary>
    public static async Task<Dictionary<string, DateTimeOffset>> ActiveAsync(RouterDbContext db, DateTimeOffset now, CancellationToken ct) =>
        // SQLite cannot compare DateTimeOffset columns in SQL; the table holds at most one row per model.
        (await db.ModelCooldowns.AsNoTracking().ToListAsync(ct))
            .Where(c => c.Until > now)
            .ToDictionary(c => c.ModelId, c => c.Until);

    internal DateTimeOffset? Until(CatalogModel model, ModelFault fault)
    {
        var limits = options.Value.Limits;
        TimeSpan? fallback = fault.Kind switch
        {
            FaultKind.Limited => limits.LimitCooldown,
            FaultKind.PlanRejected => limits.PlanRejectionCooldown,
            FaultKind.Overloaded => limits.OverloadedCooldown,
            FaultKind.Unreachable when model.Provider != ProviderKind.Local => limits.OverloadedCooldown,
            _ => null,
        };
        if (fallback is null)
        {
            return null;
        }
        var now = time.GetUtcNow();
        return fault.ResetAt is { } reset && reset > now ? Min(reset, now + MaxCooldown) : now + fallback.Value;
    }

    private async Task SaveAsync(string modelId, DateTimeOffset until, FaultKind reason)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ModelCooldowns.FindAsync(modelId);
        if (row is null)
        {
            db.ModelCooldowns.Add(new ModelCooldown { ModelId = modelId, Until = until, Reason = reason });
        }
        else if (row.Until < until)
        {
            row.Until = until;
            row.Reason = reason;
        }
        await db.SaveChangesAsync();
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
