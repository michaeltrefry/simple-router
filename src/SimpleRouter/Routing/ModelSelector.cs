using Microsoft.EntityFrameworkCore;
using SimpleRouter.Dashboard;
using SimpleRouter.Data;
using SimpleRouter.Local;

namespace SimpleRouter.Routing;

/// <summary>A selected model and the hold on its conversation place, released when the request is done.</summary>
public sealed record ModelSelection(CatalogModel Model, IDisposable Hold);

/// <summary>
/// Picks the model for a (session, tier): its existing pin if still eligible, else the next eligible tier member in
/// round-robin order, unless another pin of the same client session in the tier holds an eligible model, which the new
/// pin then shares without taking a turn. Eligible means enabled, served by a registered upstream, logged in and present in the provider's
/// discovered model list (subscriptions), not cooling after a fault (<see cref="ModelCooldowns"/>), healthy (locals) and
/// able to serve the request (<see cref="RequestNeeds"/>). Mid and Low give new sessions to eligible local models first and use subscriptions only when none is.
/// A local model already serving its limit of other conversations (<see cref="LocalConversations"/>) is skipped, and a
/// session pinned to it re-pins, unless no other model is eligible.
/// Both the pin and the per-tier round-robin cursor are persisted, so assignments survive restarts.
/// </summary>
public sealed class ModelSelector(
    IDbContextFactory<RouterDbContext> dbFactory,
    IEnumerable<IModelUpstream> upstreams,
    LocalHealth health,
    LocalConversations conversations,
    DashboardEvents events,
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

    /// <summary>
    /// Returns the session's model in <paramref name="tier"/>, assigning and persisting a pin if it has none or its pinned
    /// model is no longer eligible; null when the tier has no eligible model. Models in <paramref name="exclude"/> (already
    /// tried by this request) count as ineligible, so a failed pinned model is re-pinned. The returned hold keeps the
    /// session's place on a local model with a conversation limit until it is disposed.
    /// </summary>
    public async Task<ModelSelection?> SelectAsync(ModelTier tier, SessionIdentity session, RequestNeeds needs, CancellationToken ct,
        IReadOnlySet<string>? exclude = null)
    {
        await using (var reader = await dbFactory.CreateDbContextAsync(ct))
        {
            var (eligible, crowded) = Open(await EligibleAsync(reader, tier, needs, ct, exclude: exclude), session);
            if (eligible.Count == 0)
            {
                return null;
            }
            if (await PinnedAsync(reader, tier, session, eligible, ct) is { } pinned
                && conversations.TryEnter(pinned, session.Key, force: crowded) is { } hold)
            {
                return new ModelSelection(pinned, hold);
            }
        }

        CatalogModel model;
        IDisposable held;
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var pin = await db.SessionPins.FindAsync([session.Key, tier], ct);
            // A pinned request on the fast path can fill a model between finding it open and entering it; then choose again.
            while (true)
            {
                var all = await EligibleAsync(db, tier, needs, ct, exclude: exclude);
                var (eligible, crowded) = Open(all, session);
                if (eligible.Count == 0)
                {
                    return null;
                }

                var pinned = pin is null ? null : eligible.FirstOrDefault(m => m.Id == pin.ModelId);
                if (pinned is not null && conversations.TryEnter(pinned, session.Key, force: crowded) is { } hold)
                {
                    return new ModelSelection(pinned, hold);
                }
                if (pin is not null && all.Any(m => m.Id == pin.ModelId))
                {
                    logger.LogInformation("Model {model} is serving other conversations; moving a session in tier {tier}", pin.ModelId, tier.Name());
                }

                var sibling = pin is null ? await SiblingAsync(db, tier, session, eligible, ct) : null;
                TierCursor? cursor = null;
                if (sibling is not null)
                {
                    model = sibling;
                }
                else
                {
                    cursor = await db.TierCursors.FindAsync([tier], ct);
                    if (cursor is null)
                    {
                        cursor = new TierCursor { Tier = tier };
                        db.TierCursors.Add(cursor);
                    }
                    var pool = NewSessionPool(tier, eligible);
                    model = pool[(int)(cursor.Next % pool.Count)];
                }
                if (conversations.TryEnter(model, session.Key, force: crowded) is { } entered)
                {
                    held = entered;
                    if (cursor is not null)
                    {
                        cursor.Next++;
                    }
                    break;
                }
            }

            if (pin is not null && pin.ModelId != model.Id)
            {
                // The old model no longer counts this conversation, or it would stay closed to others for the idle window.
                conversations.Leave(pin.ModelId, session.Key);
            }
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
                // The pinned model was disabled, removed from the tier, went unhealthy, is cooling, failed this request or cannot serve it.
                pin.ModelId = model.Id;
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                held.Dispose();
                throw;
            }
            logger.LogInformation("Pinned a session to {model} in tier {tier}", model.Id, tier.Name());
        }
        finally
        {
            _gate.Release();
        }
        events.Publish();
        return new ModelSelection(model, held);
    }

    /// <summary>The session's pinned model if still eligible, else the model its next round-robin turn would get; never assigns.</summary>
    public async Task<CatalogModel?> PeekAsync(ModelTier tier, SessionIdentity session, RequestNeeds needs, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (eligible, _) = Open(await EligibleAsync(db, tier, needs, ct), session);
        if (eligible.Count == 0)
        {
            return null;
        }
        if (await PinnedAsync(db, tier, session, eligible, ct) is { } pinned)
        {
            return pinned;
        }
        if (await SiblingAsync(db, tier, session, eligible, ct) is { } sibling)
        {
            return sibling;
        }
        var cursor = await db.TierCursors.AsNoTracking().FirstOrDefaultAsync(c => c.Tier == tier, ct);
        var pool = NewSessionPool(tier, eligible);
        return pool[(int)((cursor?.Next ?? 0) % pool.Count)];
    }

    /// <summary>
    /// The eligible models that admit this session's conversation; all of them, flagged crowded, when none does, so a
    /// busy local model still serves rather than the tier going empty.
    /// </summary>
    private (List<CatalogModel> Eligible, bool Crowded) Open(List<CatalogModel> eligible, SessionIdentity session)
    {
        var open = eligible.Where(m => conversations.Admits(m, session.Key)).ToList();
        return open.Count > 0 || eligible.Count == 0 ? (open, false) : (eligible, true);
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

    /// <summary>
    /// The eligible model of the client session's earliest other pin in <paramref name="tier"/>, so a session's side
    /// requests and sub-agents start where it started and only its first pin takes a round-robin turn.
    /// </summary>
    private static async Task<CatalogModel?> SiblingAsync(RouterDbContext db, ModelTier tier, SessionIdentity session, List<CatalogModel> eligible, CancellationToken ct)
    {
        if (session.ClientSessionId is null)
        {
            return null;
        }
        // SQLite cannot order by DateTimeOffset, so the few sibling pins are ordered in memory.
        var siblings = await db.SessionPins.AsNoTracking()
            .Where(p => p.ClientSessionId == session.ClientSessionId && p.Tier == tier)
            .ToListAsync(ct);
        return siblings.OrderBy(p => p.CreatedAt)
            .Select(p => eligible.FirstOrDefault(m => m.Id == p.ModelId))
            .FirstOrDefault(m => m is not null);
    }

    /// <summary>
    /// The subscriptions whose login would make a model in <paramref name="tier"/> eligible (none stored, or the stored one
    /// was just rejected), for an empty tier's error.
    /// </summary>
    public async Task<List<ProviderKind>> MissingLoginsAsync(ModelTier tier, RequestNeeds needs, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var status = await StatusOfAsync(db, ct);
        return (await MembersAsync(db, tier, ct))
            .Where(m => _wired.Contains(m.Provider) && status(m) is ModelStatus.NotLoggedIn or ModelStatus.AuthRejected && needs.CanUse(m))
            .Select(m => m.Provider)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The earliest moment a cooling model of <paramref name="tier"/> that could otherwise serve this request becomes
    /// eligible again; null when none is cooling, so the tier is empty for reasons other than usage.
    /// </summary>
    public async Task<DateTimeOffset?> EarliestResetAsync(ModelTier tier, RequestNeeds needs, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cooling = await ModelCooldowns.ActiveAsync(db, time.GetUtcNow(), ct);
        var status = await StatusOfAsync(db, ct);
        var resets = (await MembersAsync(db, tier, ct))
            .Where(m => _wired.Contains(m.Provider) && status(m) is ModelStatus.Cooling or ModelStatus.AuthRejected && needs.CanUse(m))
            .Select(m => cooling[m.Id].Until)
            .ToList();
        return resets.Count == 0 ? null : resets.Min();
    }

    /// <summary>Every catalog model's status, for the dashboard.</summary>
    public async Task<List<ModelStatusEntry>> StatusesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var models = await db.Models.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);
        var cooling = await ModelCooldowns.ActiveAsync(db, time.GetUtcNow(), ct);
        var status = await StatusOfAsync(db, ct);
        return models.Select(m => status(m) is var s && s is ModelStatus.Cooling or ModelStatus.AuthRejected
            ? new ModelStatusEntry(m.Id, m.Provider, s, cooling[m.Id].Until)
            : new ModelStatusEntry(m.Id, m.Provider, s)).ToList();
    }

    private static Task<List<CatalogModel>> MembersAsync(RouterDbContext db, ModelTier tier, CancellationToken ct) =>
        db.TierMembers
            .Where(t => t.Tier == tier)
            .Join(db.Models, t => t.ModelId, m => m.Id, (t, m) => new { t.Position, Model = m })
            .Where(x => x.Model.Enabled)
            .OrderBy(x => x.Position)
            .Select(x => x.Model)
            .AsNoTracking()
            .ToListAsync(ct);

    private async Task<List<CatalogModel>> EligibleAsync(RouterDbContext db, ModelTier tier, RequestNeeds needs, CancellationToken ct,
        IReadOnlySet<string>? exclude = null)
    {
        var members = await MembersAsync(db, tier, ct);
        var status = await StatusOfAsync(db, ct);
        return members.Where(m => _wired.Contains(m.Provider)
                && status(m) == ModelStatus.Eligible
                && needs.CanUse(m)
                && exclude?.Contains(m.Id) != true)
            .ToList();
    }

    /// <summary>Lists and logins are read per selection, never cached: login and its model list fetch run in a separate CLI process.</summary>
    private async Task<Func<CatalogModel, ModelStatus>> StatusOfAsync(RouterDbContext db, CancellationToken ct)
    {
        var loggedIn = await LoggedInAsync(db, ct);
        var cooling = await ModelCooldowns.ActiveAsync(db, time.GetUtcNow(), ct);
        // A provider without a successfully fetched list allows every model, so routing never goes dark before the first fetch.
        var lists = (await db.ProviderModelLists.AsNoTracking().ToListAsync(ct))
            .Where(l => l.ModelIds is not null)
            .ToDictionary(l => l.Provider, l => (IReadOnlyCollection<string>)l.ModelIds!.ToHashSet(StringComparer.OrdinalIgnoreCase));
        return m =>
        {
            if (!m.Enabled)
            {
                return ModelStatus.Disabled;
            }
            if (!loggedIn(m.Provider))
            {
                return ModelStatus.NotLoggedIn;
            }
            if (m.Provider != ProviderKind.Local && lists.TryGetValue(m.Provider, out var list) && !ModelDiscovery.Lists(m.Provider, list, m.UpstreamModelId))
            {
                return ModelStatus.UnknownModel;
            }
            if (cooling.TryGetValue(m.Id, out var cooldown))
            {
                return cooldown.Reason == FaultKind.AuthRejected ? ModelStatus.AuthRejected : ModelStatus.Cooling;
            }
            return m.Provider is ProviderKind.Local or ProviderKind.SystemOne && !health.IsHealthy(m.Id) ? ModelStatus.Unhealthy : ModelStatus.Eligible;
        };
    }

    internal static async Task<Func<ProviderKind, bool>> LoggedInAsync(RouterDbContext db, CancellationToken ct)
    {
        var hasClaude = await db.ClaudeCredentials.AnyAsync(ct);
        var hasCodex = await db.CodexCredentials.AnyAsync(ct);
        var hasOpenRouter = await db.ProviderCredentials.AnyAsync(c => c.Provider == ProviderKind.OpenRouter && c.ProtectedApiKey != "", ct);
        return provider => provider switch
        {
            ProviderKind.Claude => hasClaude,
            ProviderKind.Codex => hasCodex,
            ProviderKind.OpenRouter => hasOpenRouter,
            _ => true
        };
    }
}
