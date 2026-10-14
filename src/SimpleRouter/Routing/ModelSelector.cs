using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Local;

namespace SimpleRouter.Routing;

/// <summary>
/// Picks the model for a (session, tier): its existing pin if still eligible, else the next eligible tier member in
/// round-robin order. Eligible means enabled, served by a registered upstream, healthy (locals) and able to serve the
/// request (<see cref="RequestNeeds"/>). Mid and Low give new sessions to eligible local models first and use subscriptions only when none is.
/// Both the pin and the per-tier round-robin cursor are persisted, so assignments survive restarts.
/// </summary>
public sealed class ModelSelector(
    IDbContextFactory<RouterDbContext> dbFactory,
    IEnumerable<IModelUpstream> upstreams,
    LocalHealth health,
    TimeProvider time,
    ILogger<ModelSelector> logger)
{
    private readonly HashSet<ProviderKind> _wired = upstreams.Select(u => u.Provider).ToHashSet();

    // Serializes assignment so concurrent first requests of one session share a pin and a cursor step; pinned reads skip it.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TierRules> GetTierRulesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var aliases = await db.TierAliases.OrderBy(a => a.Position).Select(a => new { a.Pattern, a.Tier }).ToListAsync(ct);
        var setting = await db.Settings.FindAsync([CatalogSeeder.DefaultTierSetting], ct);
        var defaultTier = ModelTiers.TryParse(setting?.Value, out var configured) ? configured : ModelTier.High;
        return new TierRules(aliases.Select(a => (a.Pattern, a.Tier)).ToList(), defaultTier);
    }

    /// <summary>Returns the session's model in <paramref name="tier"/>, assigning and persisting a pin if it has none; null when the tier has no eligible model.</summary>
    public async Task<CatalogModel?> SelectAsync(ModelTier tier, SessionIdentity session, RequestNeeds needs, CancellationToken ct)
    {
        await using (var reader = await dbFactory.CreateDbContextAsync(ct))
        {
            var eligible = await EligibleAsync(reader, tier, needs, ct);
            if (eligible.Count == 0)
            {
                return null;
            }
            if (await PinnedAsync(reader, tier, session, eligible, ct) is { } pinned)
            {
                return pinned;
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var eligible = await EligibleAsync(db, tier, needs, ct);
            if (eligible.Count == 0)
            {
                return null;
            }

            var pin = await db.SessionPins.FindAsync([session.Key, tier], ct);
            var pinned = pin is null ? null : eligible.FirstOrDefault(m => m.Id == pin.ModelId);
            if (pinned is not null)
            {
                return pinned;
            }

            var cursor = await db.TierCursors.FindAsync([tier], ct);
            if (cursor is null)
            {
                cursor = new TierCursor { Tier = tier };
                db.TierCursors.Add(cursor);
            }
            var pool = NewSessionPool(tier, eligible);
            var model = pool[(int)(cursor.Next % pool.Count)];
            cursor.Next++;

            if (pin is null)
            {
                db.SessionPins.Add(new SessionPin
                {
                    SessionKey = session.Key,
                    Tier = tier,
                    ModelId = model.Id,
                    ClientSessionId = session.ClientSessionId,
                    CreatedAt = time.GetUtcNow(),
                });
            }
            else
            {
                // The pinned model was disabled, removed from the tier, went unhealthy or cannot serve this request.
                pin.ModelId = model.Id;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Pinned a session to {model} in tier {tier}", model.Id, tier.Name());
            return model;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The session's pinned model if still eligible, else the model its next round-robin turn would get; never assigns.</summary>
    public async Task<CatalogModel?> PeekAsync(ModelTier tier, SessionIdentity session, RequestNeeds needs, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var eligible = await EligibleAsync(db, tier, needs, ct);
        if (eligible.Count == 0)
        {
            return null;
        }
        if (await PinnedAsync(db, tier, session, eligible, ct) is { } pinned)
        {
            return pinned;
        }
        var cursor = await db.TierCursors.AsNoTracking().FirstOrDefaultAsync(c => c.Tier == tier, ct);
        var pool = NewSessionPool(tier, eligible);
        return pool[(int)((cursor?.Next ?? 0) % pool.Count)];
    }

    private static List<CatalogModel> NewSessionPool(ModelTier tier, List<CatalogModel> eligible)
    {
        if (tier is ModelTier.Mid or ModelTier.Low)
        {
            var locals = eligible.Where(m => m.Provider == ProviderKind.Local).ToList();
            if (locals.Count > 0)
            {
                return locals;
            }
        }
        return eligible;
    }

    private static async Task<CatalogModel?> PinnedAsync(RouterDbContext db, ModelTier tier, SessionIdentity session, List<CatalogModel> eligible, CancellationToken ct)
    {
        var pin = await db.SessionPins.AsNoTracking().FirstOrDefaultAsync(p => p.SessionKey == session.Key && p.Tier == tier, ct);
        return eligible.FirstOrDefault(m => m.Id == pin?.ModelId);
    }

    private async Task<List<CatalogModel>> EligibleAsync(RouterDbContext db, ModelTier tier, RequestNeeds needs, CancellationToken ct)
    {
        var members = await db.TierMembers
            .Where(t => t.Tier == tier)
            .Join(db.Models, t => t.ModelId, m => m.Id, (t, m) => new { t.Position, Model = m })
            .Where(x => x.Model.Enabled)
            .OrderBy(x => x.Position)
            .Select(x => x.Model)
            .AsNoTracking()
            .ToListAsync(ct);
        return members.Where(m => _wired.Contains(m.Provider)
                && needs.CanUse(m)
                && (m.Provider != ProviderKind.Local || health.IsHealthy(m.Id)))
            .ToList();
    }
}
