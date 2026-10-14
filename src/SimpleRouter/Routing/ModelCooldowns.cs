using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleRouter.Data;

namespace SimpleRouter.Routing;

/// <summary>
/// Persists how long a faulted model is skipped. A provider-reported reset wins; otherwise the configured cooldown for
/// the fault applies. Context overflow and an unreachable local endpoint (health handles it) cool nothing. A rejected
/// account cools every model of its provider.
/// </summary>
public sealed class ModelCooldowns(
    IDbContextFactory<RouterDbContext> dbFactory,
    IOptions<RouterOptions> options,
    TimeProvider time,
    ILogger<ModelCooldowns> logger)
{
    // Subscription windows are at most a week; a later reported reset is a misread header, not a reason to bench a model.
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromDays(8);

    /// <summary>Records <paramref name="fault"/> for <paramref name="model"/>; an existing longer cooldown is kept.</summary>
    public async Task RecordAsync(CatalogModel model, ModelFault fault)
    {
        if (Until(model, fault) is not { } until)
        {
            return;
        }
        if (fault.Kind == FaultKind.AuthRejected)
        {
            logger.LogWarning("{provider} rejected the subscription account; skipping its models until {until:u}", model.Provider, until.UtcDateTime);
        }
        // Not the request's token: a client that disconnects after a 429 must not erase the limit it revealed.
        try
        {
            await SaveAsync(model, until, fault.Kind);
        }
        catch (DbUpdateException)
        {
            // A concurrent request inserted the row first.
            try
            {
                await SaveAsync(model, until, fault.Kind);
            }
            catch (DbUpdateException e)
            {
                // The response is already decided; a lost cooldown only means the model is tried again.
                logger.LogWarning("Could not record the cooldown of model {model}: {error}", model.Id, e.GetType().Name);
                return;
            }
        }
        logger.LogWarning("Model {model} is cooling until {until:u} ({reason})", model.Id, until.UtcDateTime, fault.Kind);
    }

    /// <summary>Cooldowns still running, by model.</summary>
    public static async Task<Dictionary<string, ModelCooldown>> ActiveAsync(RouterDbContext db, DateTimeOffset now, CancellationToken ct) =>
        // SQLite cannot compare DateTimeOffset columns in SQL; the table holds at most one row per model.
        (await db.ModelCooldowns.AsNoTracking().ToListAsync(ct))
            .Where(c => c.Until > now)
            .ToDictionary(c => c.ModelId);

    internal DateTimeOffset? Until(CatalogModel model, ModelFault fault)
    {
        var limits = options.Value.Limits;
        TimeSpan? fallback = fault.Kind switch
        {
            FaultKind.Limited => limits.LimitCooldown,
            FaultKind.PlanRejected => limits.PlanRejectionCooldown,
            FaultKind.Overloaded => limits.OverloadedCooldown,
            FaultKind.Unreachable when model.Provider != ProviderKind.Local => limits.OverloadedCooldown,
            FaultKind.AuthRejected => limits.AuthRejectionCooldown,
            _ => null,
        };
        if (fallback is null)
        {
            return null;
        }
        var now = time.GetUtcNow();
        return fault.ResetAt is { } reset && reset > now ? Min(reset, now + MaxCooldown) : now + fallback.Value;
    }

    private async Task SaveAsync(CatalogModel model, DateTimeOffset until, FaultKind reason)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = reason == FaultKind.AuthRejected
            ? await db.Models.Where(m => m.Provider == model.Provider).Select(m => m.Id).ToListAsync()
            : [model.Id];
        foreach (var id in ids)
        {
            var row = await db.ModelCooldowns.FindAsync(id);
            if (row is null)
            {
                db.ModelCooldowns.Add(new ModelCooldown { ModelId = id, Until = until, Reason = reason });
            }
            else if (row.Until < until)
            {
                row.Until = until;
                row.Reason = reason;
            }
        }
        await db.SaveChangesAsync();
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
