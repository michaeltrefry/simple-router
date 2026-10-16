using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Local;
using SimpleRouter.Routing;

namespace SimpleRouter.Dashboard;

/// <summary>
/// Edits the routing catalog from the dashboard. Selection, tier rules, prices and effort mappings are read from the
/// database per request, so a saved edit applies to the next new session; endpoint health is the one in-memory state, so
/// every local or SystemOne edit re-probes before it returns.
/// </summary>
public sealed partial class CatalogSettings(
    IDbContextFactory<RouterDbContext> dbFactory,
    SecretProtector secrets,
    LocalHealth health,
    DashboardEvents events)
{
    /// <summary>Alias globs run against every request's model; these caps bound their matching cost.</summary>
    public const int MaxAliasPatternLength = 200, MaxAliasWildcards = 4;

    /// <summary>USD per million tokens; far above any real price, low enough that recorded costs stay in range.</summary>
    public const decimal MaxPricePerMTok = 1_000_000;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/@-]{0,199}$")]
    private static partial Regex ModelIdPattern();

    public async Task<SettingsSnapshot> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var models = await db.Models.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);
        var members = await db.TierMembers.AsNoTracking().OrderBy(m => m.Position).ToListAsync(ct);
        var aliases = await db.TierAliases.AsNoTracking().OrderBy(a => a.Position).ToListAsync(ct);
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == CatalogSeeder.DefaultTierSetting, ct);
        var prices = await db.ModelPrices.AsNoTracking().ToDictionaryAsync(p => p.ModelId, ct);
        var lists = await db.ProviderModelLists.AsNoTracking().ToListAsync(ct);

        var tiers = Enum.GetValues<ModelTier>().ToDictionary(t => t,
            t => (IReadOnlyList<string>)members.Where(m => m.Tier == t).Select(m => m.ModelId).ToList());
        return new SettingsSnapshot(
            models.Select(m => new ModelRow(m.Id, m.Provider, m.UpstreamModelId, m.Enabled, m.BaseUrl, m.ProtectedApiKey is not null,
                m.ContextWindow, m.SupportsTools, m.SupportsVision, m.Effort,
                tiers.Where(t => t.Value.Contains(m.Id)).Select(t => t.Key).ToList(), m.MaxPromptTokens)).ToList(),
            tiers,
            aliases.Select(a => new AliasRow(a.Pattern, a.Tier)).ToList(),
            ModelTiers.TryParse(setting?.Value, out var defaultTier) ? defaultTier : ModelTier.High,
            models.Select(m => prices.TryGetValue(m.Id, out var p)
                ? new PriceRow(m.Id, p.InputPerMTok, p.OutputPerMTok, p.CacheReadPerMTok, p.CacheWritePerMTok, p.CacheWrite1hPerMTok)
                : new PriceRow(m.Id, 0, 0, 0, 0, 0, Stored: false)).ToList(),
            lists.Where(l => l.ModelIds is not null).ToDictionary(l => l.Provider, l => (IReadOnlyList<string>)l.ModelIds!));
    }

    public async Task SetEnabledAsync(string modelId, bool enabled, CancellationToken ct = default)
    {
        ProviderKind provider;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var model = await FindAsync(db, modelId, ct);
            model.Enabled = enabled;
            provider = model.Provider;
            await db.SaveChangesAsync(ct);
        }
        await ChangedAsync(Probed(provider), ct);
    }

    public async Task AddToTierAsync(ModelTier tier, string modelId, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var model = await FindAsync(db, modelId, ct);
            if (model.Provider == ProviderKind.SystemOne)
            {
                throw new SettingsException($"'{modelId}' is a SystemOne decision model: it answers /v1/systemone and cannot join a tier.");
            }
            await AddMemberAsync(db, tier, model.Id, ct);
            await db.SaveChangesAsync(ct);
        }
        await ChangedAsync(false, ct);
    }

    /// <summary>Adds a subscription model to a tier by its upstream ID, creating its catalog entry (enabled) when it has none.</summary>
    public async Task AddSubscriptionModelToTierAsync(ModelTier tier, ProviderKind provider, string upstreamModelId, CancellationToken ct = default)
    {
        if (provider is ProviderKind.Local or ProviderKind.SystemOne)
        {
            throw new SettingsException("Local and SystemOne models are added with their model forms.");
        }
        var id = ValidModelId(upstreamModelId, "Model ID");
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var model = await db.Models.FirstOrDefaultAsync(m => m.Provider == provider && m.UpstreamModelId == id, ct);
            if (model is null)
            {
                if (await db.Models.AnyAsync(m => m.Id == id, ct))
                {
                    throw new SettingsException($"A model with ID '{id}' already exists for another provider.");
                }
                model = new CatalogModel { Id = id, Provider = provider, UpstreamModelId = id, Enabled = true };
                db.Models.Add(model);
            }
            await AddMemberAsync(db, tier, model.Id, ct);
            await db.SaveChangesAsync(ct);
        }
        await ChangedAsync(false, ct);
    }

    public async Task RemoveFromTierAsync(ModelTier tier, string modelId, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.TierMembers.Where(m => m.Tier == tier && m.ModelId == modelId).ExecuteDeleteAsync(ct);
        }
        await ChangedAsync(false, ct);
    }

    /// <summary>Replaces the alias table; earlier entries win.</summary>
    public async Task SaveAliasesAsync(IReadOnlyList<AliasRow> aliases, CancellationToken ct = default)
    {
        var rows = aliases.Select(a => a with { Pattern = a.Pattern.Trim() }).ToList();
        if (rows.Any(a => a.Pattern.Length == 0))
        {
            throw new SettingsException("An alias pattern cannot be empty.");
        }
        if (rows.FirstOrDefault(a => a.Pattern.Length > MaxAliasPatternLength || a.Pattern.Count(c => c == '*') > MaxAliasWildcards) is { } costly)
        {
            throw new SettingsException(
                $"The alias pattern '{costly.Pattern}' is too long or has too many '*': at most {MaxAliasPatternLength} characters and {MaxAliasWildcards} '*'.");
        }
        if (rows.Any(a => !Enum.IsDefined(a.Tier)))
        {
            throw new SettingsException("An alias names an unknown tier.");
        }
        if (rows.GroupBy(a => a.Pattern).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new SettingsException($"The alias pattern '{duplicate.Key}' is listed twice.");
        }
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.TierAliases.RemoveRange(await db.TierAliases.ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            db.TierAliases.AddRange(rows.Select((a, i) => new TierAlias { Pattern = a.Pattern, Tier = a.Tier, Position = i }));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        await ChangedAsync(false, ct);
    }

    public async Task SetDefaultTierAsync(ModelTier tier, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(tier))
        {
            throw new SettingsException("Unknown tier.");
        }
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var setting = await db.Settings.FindAsync([CatalogSeeder.DefaultTierSetting], ct);
            if (setting is null)
            {
                db.Settings.Add(new Setting { Key = CatalogSeeder.DefaultTierSetting, Value = tier.Name() });
            }
            else
            {
                setting.Value = tier.Name();
            }
            await db.SaveChangesAsync(ct);
        }
        await ChangedAsync(false, ct);
    }

    public async Task SavePriceAsync(PriceRow price, CancellationToken ct = default)
    {
        var all = new[] { price.Input, price.Output, price.CacheRead, price.CacheWrite, price.CacheWrite1h };
        if (all.Any(p => p < 0))
        {
            throw new SettingsException("Prices cannot be negative.");
        }
        if (all.Any(p => p > MaxPricePerMTok))
        {
            throw new SettingsException($"Prices cannot exceed {MaxPricePerMTok:N0} USD per million tokens.");
        }
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await FindAsync(db, price.ModelId, ct);
            var row = await db.ModelPrices.FindAsync([price.ModelId], ct);
            if (row is null)
            {
                row = new ModelPrice { ModelId = price.ModelId };
                db.ModelPrices.Add(row);
            }
            row.InputPerMTok = price.Input;
            row.OutputPerMTok = price.Output;
            row.CacheReadPerMTok = price.CacheRead;
            row.CacheWritePerMTok = price.CacheWrite;
            row.CacheWrite1hPerMTok = price.CacheWrite1h;
            await db.SaveChangesAsync(ct);
        }
        await ChangedAsync(false, ct);
    }

    /// <summary>
    /// Adds a local model (<paramref name="existingId"/> null) or updates <paramref name="existingId"/>, renaming it when the
    /// input's ID differs, with its tier membership, then probes it.
    /// </summary>
    public async Task SaveLocalModelAsync(LocalModelInput input, string? existingId, CancellationToken ct = default)
    {
        var id = ValidModelId(input.Id, "Model ID");
        var upstreamId = input.UpstreamModelId.Trim();
        if (upstreamId.Length == 0)
        {
            throw new SettingsException("Upstream model ID is required.");
        }
        var baseUrl = ValidBaseUrl(input.BaseUrl);
        if (input.ContextWindow is <= 0)
        {
            throw new SettingsException("Context window must be a positive number of tokens, or empty for unlimited.");
        }
        if (input.MaxPromptTokens is <= 0)
        {
            throw new SettingsException("Max prompt must be a positive number of tokens, or empty for no cap.");
        }
        ValidEffort(input.Effort, id);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            CatalogModel? model;
            if (existingId is null)
            {
                model = await db.Models.FindAsync([id], ct);
                if (model is not null)
                {
                    throw new SettingsException($"A model with ID '{id}' already exists.");
                }
                model = new CatalogModel { Id = id, Provider = ProviderKind.Local, UpstreamModelId = upstreamId };
                db.Models.Add(model);
                db.ModelPrices.Add(new ModelPrice { ModelId = id });
            }
            else
            {
                model = await db.Models.FindAsync([existingId], ct);
                if (model is not { Provider: ProviderKind.Local })
                {
                    throw new SettingsException($"'{existingId}' is not a local model.");
                }
                if (existingId != id)
                {
                    model = await RenameAsync(db, model, id, ct);
                }
            }
            model.UpstreamModelId = upstreamId;
            model.BaseUrl = baseUrl;
            model.ContextWindow = input.ContextWindow;
            model.MaxPromptTokens = input.MaxPromptTokens;
            model.SupportsTools = input.SupportsTools;
            model.SupportsVision = input.SupportsVision;
            model.Enabled = input.Enabled;
            model.Effort = input.Effort;
            if (!string.IsNullOrEmpty(input.NewApiKey))
            {
                model.ProtectedApiKey = secrets.Protect(input.NewApiKey);
            }
            else if (input.ClearApiKey)
            {
                model.ProtectedApiKey = null;
            }

            var current = await db.TierMembers.Where(m => m.ModelId == id).ToListAsync(ct);
            db.TierMembers.RemoveRange(current.Where(m => !input.Tiers.Contains(m.Tier)));
            foreach (var tier in input.Tiers.Distinct().Where(t => current.All(m => m.Tier != t)))
            {
                await AddMemberAsync(db, tier, id, ct);
            }
            await db.SaveChangesAsync(ct);
            if (existingId is not null && existingId != id)
            {
                await health.RenameAsync(existingId, id, () => transaction.CommitAsync(ct), ct);
            }
            else
            {
                await transaction.CommitAsync(ct);
            }
        }
        await ChangedAsync(true, ct);
    }

    /// <summary>
    /// Adds a SystemOne decision model (<paramref name="existingId"/> null) or updates <paramref name="existingId"/>, renaming
    /// it when the input's ID differs, then probes it. It never joins a tier.
    /// </summary>
    public async Task SaveSystemOneModelAsync(SystemOneModelInput input, string? existingId, CancellationToken ct = default)
    {
        var id = ValidModelId(input.Id, "Model ID");
        var upstreamId = input.UpstreamModelId.Trim();
        if (upstreamId.Length == 0)
        {
            throw new SettingsException("Upstream model ID is required.");
        }
        var baseUrl = ValidBaseUrl(input.BaseUrl);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            CatalogModel? model;
            if (existingId is null)
            {
                if (await db.Models.FindAsync([id], ct) is not null)
                {
                    throw new SettingsException($"A model with ID '{id}' already exists.");
                }
                model = new CatalogModel { Id = id, Provider = ProviderKind.SystemOne, UpstreamModelId = upstreamId };
                db.Models.Add(model);
                db.ModelPrices.Add(new ModelPrice { ModelId = id });
            }
            else
            {
                model = await db.Models.FindAsync([existingId], ct);
                if (model is not { Provider: ProviderKind.SystemOne })
                {
                    throw new SettingsException($"'{existingId}' is not a SystemOne model.");
                }
                if (existingId != id)
                {
                    model = await RenameAsync(db, model, id, ct);
                }
            }
            model.UpstreamModelId = upstreamId;
            model.BaseUrl = baseUrl;
            model.Enabled = input.Enabled;
            if (!string.IsNullOrEmpty(input.NewApiKey))
            {
                model.ProtectedApiKey = secrets.Protect(input.NewApiKey);
            }
            else if (input.ClearApiKey)
            {
                model.ProtectedApiKey = null;
            }
            await db.SaveChangesAsync(ct);
            if (existingId is not null && existingId != id)
            {
                await health.RenameAsync(existingId, id, () => transaction.CommitAsync(ct), ct);
            }
            else
            {
                await transaction.CommitAsync(ct);
            }
        }
        await ChangedAsync(true, ct);
    }

    /// <summary>
    /// Moves a local or SystemOne model to <paramref name="newId"/> with everything keyed by its ID, so its pinned sessions stay on it
    /// and its usage history follows it.
    /// </summary>
    private static async Task<CatalogModel> RenameAsync(RouterDbContext db, CatalogModel model, string newId, CancellationToken ct)
    {
        var oldId = model.Id;
        var ids = await db.Models.Select(m => m.Id).ToListAsync(ct);
        if (ids.Any(m => m != oldId && string.Equals(m, newId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SettingsException($"A model with ID '{newId}' already exists.");
        }
        var lists = await db.ProviderModelLists.AsNoTracking().ToListAsync(ct);
        if (lists.Any(l => l.ModelIds?.Contains(newId, StringComparer.OrdinalIgnoreCase) == true))
        {
            throw new SettingsException($"'{newId}' is a subscription model ID.");
        }

        db.Entry(model).State = EntityState.Detached;
        model.Id = newId;
        db.Models.Add(model);
        await db.SaveChangesAsync(ct);
        // Rows left for an ID that was never in the catalog would collide with the moved ones.
        await db.ModelPrices.Where(p => p.ModelId == newId).ExecuteDeleteAsync(ct);
        await db.ModelCooldowns.Where(c => c.ModelId == newId).ExecuteDeleteAsync(ct);
        await db.TierMembers.Where(m => m.ModelId == oldId).ExecuteUpdateAsync(u => u.SetProperty(m => m.ModelId, newId), ct);
        await db.ModelPrices.Where(p => p.ModelId == oldId).ExecuteUpdateAsync(u => u.SetProperty(p => p.ModelId, newId), ct);
        await db.ModelCooldowns.Where(c => c.ModelId == oldId).ExecuteUpdateAsync(u => u.SetProperty(c => c.ModelId, newId), ct);
        await db.SessionPins.Where(p => p.ModelId == oldId).ExecuteUpdateAsync(u => u.SetProperty(p => p.ModelId, newId), ct);
        await db.RequestUsages.Where(r => r.ModelId == oldId).ExecuteUpdateAsync(u => u.SetProperty(r => r.ModelId, newId), ct);
        await db.Models.Where(m => m.Id == oldId).ExecuteDeleteAsync(ct);
        return model;
    }

    /// <summary>Removes a local or SystemOne model; subscription models are only disabled or taken out of tiers.</summary>
    public async Task RemoveModelAsync(string modelId, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var model = await FindAsync(db, modelId, ct);
            if (!Probed(model.Provider))
            {
                throw new SettingsException($"'{modelId}' is not a local or SystemOne model.");
            }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.TierMembers.Where(m => m.ModelId == modelId).ExecuteDeleteAsync(ct);
            await db.ModelPrices.Where(p => p.ModelId == modelId).ExecuteDeleteAsync(ct);
            await db.ModelCooldowns.Where(c => c.ModelId == modelId).ExecuteDeleteAsync(ct);
            db.Models.Remove(model);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        await ChangedAsync(true, ct);
    }

    /// <summary>
    /// Probes <c>GET {baseUrl}/models</c> with the typed key, else the stored key of <paramref name="existingId"/> unless
    /// <paramref name="clearApiKey"/>; returns why it failed, or null when it answered.
    /// </summary>
    public async Task<string?> TestConnectionAsync(string baseUrl, string? newApiKey, string? existingId, bool clearApiKey = false,
        CancellationToken ct = default)
    {
        var url = ValidBaseUrl(baseUrl);
        var key = newApiKey;
        if (string.IsNullOrEmpty(key) && !clearApiKey && existingId is not null)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var stored = await db.Models.AsNoTracking().Where(m => m.Id == existingId).Select(m => m.ProtectedApiKey).FirstOrDefaultAsync(ct);
            key = stored is null ? null : secrets.Unprotect(stored);
        }
        return await health.TestAsync(url, key, ct);
    }

    /// <summary>Providers whose endpoints <see cref="LocalHealth"/> probes.</summary>
    private static bool Probed(ProviderKind provider) => provider is ProviderKind.Local or ProviderKind.SystemOne;

    private async Task ChangedAsync(bool probeLocals, CancellationToken ct)
    {
        if (probeLocals)
        {
            await health.CheckAsync(ct);
        }
        events.Publish();
    }

    private static async Task<CatalogModel> FindAsync(RouterDbContext db, string modelId, CancellationToken ct) =>
        await db.Models.FindAsync([modelId], ct) ?? throw new SettingsException($"Unknown model '{modelId}'.");

    private static async Task AddMemberAsync(RouterDbContext db, ModelTier tier, string modelId, CancellationToken ct)
    {
        if (!Enum.IsDefined(tier))
        {
            throw new SettingsException("Unknown tier.");
        }
        var members = await db.TierMembers.Where(m => m.Tier == tier).ToListAsync(ct);
        if (members.Any(m => m.ModelId == modelId))
        {
            throw new SettingsException($"'{modelId}' is already in the {tier.Name()} tier.");
        }
        db.TierMembers.Add(new TierMember { Tier = tier, ModelId = modelId, Position = members.Count == 0 ? 0 : members.Max(m => m.Position) + 1 });
    }

    private static string ValidModelId(string? value, string label)
    {
        var id = value?.Trim() ?? "";
        return ModelIdPattern().IsMatch(id)
            ? id
            : throw new SettingsException($"{label} must be 1-200 letters, digits or . _ : / @ - and start with a letter or digit.");
    }

    private static string ValidBaseUrl(string? value)
    {
        var url = value?.Trim() ?? "";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? url
            : throw new SettingsException("Base URL must be an absolute http:// or https:// URL.");
    }

    private static void ValidEffort(EffortMapping? effort, string modelId)
    {
        try
        {
            Effort.Validate(effort, modelId);
        }
        catch (InvalidDataException e)
        {
            throw new SettingsException(e.Message);
        }
    }
}
