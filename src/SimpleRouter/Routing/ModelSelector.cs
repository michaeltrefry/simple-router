using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;

namespace SimpleRouter.Routing;

/// <summary>
/// Picks the model for a (session, tier): its existing pin, else the next eligible tier member in round-robin order.
/// Both the pin and the per-tier round-robin cursor are persisted, so assignments survive restarts.
/// </summary>
public sealed class ModelSelector(IDbContextFactory<RouterDbContext> dbFactory, TimeProvider time, ILogger<ModelSelector> logger)
{
    private static readonly HashSet<ProviderKind> WiredProviders = [ProviderKind.Claude];

    // Serializes the read-then-assign so concurrent first requests of one session share a pin and a cursor step.
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
    public async Task<CatalogModel?> SelectAsync(ModelTier tier, SessionIdentity session, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var eligible = await EligibleAsync(db, tier, ct);
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
            var model = eligible[(int)(cursor.Next % eligible.Count)];
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
                // The pinned model was disabled or removed from the tier.
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

    /// <summary>The session's pinned model if still eligible, else the tier's first eligible model; never assigns.</summary>
    public async Task<CatalogModel?> PeekAsync(ModelTier tier, SessionIdentity session, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var eligible = await EligibleAsync(db, tier, ct);
        var pin = await db.SessionPins.AsNoTracking().FirstOrDefaultAsync(p => p.SessionKey == session.Key && p.Tier == tier, ct);
        return eligible.FirstOrDefault(m => m.Id == pin?.ModelId) ?? eligible.FirstOrDefault();
    }

    private static async Task<List<CatalogModel>> EligibleAsync(RouterDbContext db, ModelTier tier, CancellationToken ct)
    {
        var members = await db.TierMembers
            .Where(t => t.Tier == tier)
            .Join(db.Models, t => t.ModelId, m => m.Id, (t, m) => new { t.Position, Model = m })
            .Where(x => x.Model.Enabled)
            .OrderBy(x => x.Position)
            .Select(x => x.Model)
            .AsNoTracking()
            .ToListAsync(ct);
        return members.Where(m => WiredProviders.Contains(m.Provider)).ToList();
    }
}
