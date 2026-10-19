using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleRouter.Data;
using SimpleRouter.OpenRouter;

namespace SimpleRouter.Routing;

/// <summary>
/// Loads the model catalog, tier membership, aliases and default tier from the seed file into an empty database, then
/// applies the optional per-machine override file to existing models whenever its content has changed.
/// </summary>
public sealed class CatalogSeeder(
    IDbContextFactory<RouterDbContext> dbFactory,
    IOptions<RouterOptions> options,
    SecretProtector secrets,
    ILogger<CatalogSeeder> logger)
{
    public const string DefaultTierSetting = "default_tier";

    /// <summary>Set once effort mappings have been taken from the seed, so a mapping cleared afterwards stays cleared.</summary>
    public const string EffortSeededSetting = "effort_seeded";

    /// <summary>SHA-256 of the override file content last applied.</summary>
    public const string OverrideHashSetting = "seed_override_hash";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private sealed record SeedModel(
        string Id,
        string Provider,
        string UpstreamModelId,
        bool Enabled,
        string? BaseUrl = null,
        string? ApiKey = null,
        int? ContextWindow = null,
        int? MaxPromptTokens = null,
        int? MaxConversations = null,
        bool SupportsTools = true,
        bool SupportsVision = false,
        EffortMapping? Effort = null,
        OpenRouterRoutingOptions? OpenRouter = null);

    private sealed record SeedAlias(string Pattern, string Tier);

    /// <summary><c>cacheWrite1h</c> defaults to <c>cacheWrite</c>, for providers whose cache has one lifetime.</summary>
    private sealed record SeedPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, decimal? CacheWrite1h = null);

    private sealed record Seed(string DefaultTier, SeedModel[] Models, Dictionary<string, string[]> Tiers, SeedAlias[] Aliases,
        Dictionary<string, SeedPrice>? Prices = null);

    private sealed record ModelOverride(
        string Id,
        bool? Enabled,
        string? BaseUrl,
        string? UpstreamModelId,
        string? ApiKey,
        int? ContextWindow,
        int? MaxPromptTokens,
        int? MaxConversations,
        bool? SupportsTools,
        bool? SupportsVision,
        EffortMapping? Effort,
        OpenRouterRoutingOptions? OpenRouter);

    private sealed record Overrides(ModelOverride[]? Models);

    private sealed record StoredEffort(string Id, string Effort);

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedEmptyAsync(ct);
        await SeedPricesAsync(ct);
        await ApplyOverridesAsync(ct);
        await WarnUnreadableEffortAsync(ct);
    }

    /// <summary>Seeds the price table once, separately from the catalog, so a database created before prices existed gets them.</summary>
    private async Task SeedPricesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.ModelPrices.AnyAsync(ct))
        {
            return;
        }
        var seed = await ReadSeedAsync(ct);
        db.ModelPrices.AddRange((seed.Prices ?? []).Select(p => new ModelPrice
        {
            ModelId = p.Key,
            InputPerMTok = p.Value.Input,
            OutputPerMTok = p.Value.Output,
            CacheReadPerMTok = p.Value.CacheRead,
            CacheWritePerMTok = p.Value.CacheWrite,
            CacheWrite1hPerMTok = p.Value.CacheWrite1h ?? p.Value.CacheWrite,
        }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Seeds once; later edits to the tables are kept across restarts.</summary>
    private async Task SeedEmptyAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Models.AnyAsync(ct))
        {
            await BackfillEffortAsync(db, ct);
            return;
        }

        var seed = await ReadSeedAsync(ct);
        db.Models.AddRange(seed.Models.Select(m => new CatalogModel
        {
            Id = m.Id,
            Provider = Enum.Parse<ProviderKind>(m.Provider, ignoreCase: true),
            UpstreamModelId = m.UpstreamModelId,
            Enabled = m.Enabled,
            BaseUrl = m.BaseUrl,
            ProtectedApiKey = Protect(m.ApiKey),
            ContextWindow = m.ContextWindow,
            MaxPromptTokens = m.MaxPromptTokens,
            MaxConversations = m.MaxConversations,
            SupportsTools = m.SupportsTools,
            SupportsVision = m.SupportsVision,
            Effort = m.Effort,
            OpenRouter = m.OpenRouter,
        }));
        foreach (var (tier, modelIds) in seed.Tiers)
        {
            db.TierMembers.AddRange(modelIds.Select((id, i) => new TierMember { Tier = ModelTiers.Parse(tier), ModelId = id, Position = i }));
        }
        db.TierAliases.AddRange(seed.Aliases.Select((a, i) => new TierAlias { Pattern = a.Pattern, Tier = ModelTiers.Parse(a.Tier), Position = i }));
        if (await db.Settings.FindAsync([DefaultTierSetting], ct) is null)
        {
            db.Settings.Add(new Setting { Key = DefaultTierSetting, Value = ModelTiers.Parse(seed.DefaultTier).Name() });
        }
        db.Settings.Add(new Setting { Key = EffortSeededSetting, Value = "true" });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A catalog seeded before effort mappings existed takes them from the seed once, without touching mappings already set.
    /// </summary>
    private async Task BackfillEffortAsync(RouterDbContext db, CancellationToken ct)
    {
        if (!File.Exists(options.Value.SeedFile) || await db.Settings.FindAsync([EffortSeededSetting], ct) is not null)
        {
            return;
        }
        var seeded = (await ReadSeedAsync(ct)).Models.Where(m => m.Effort is not null).ToDictionary(m => m.Id, m => m.Effort);
        foreach (var model in await db.Models.ToListAsync(ct))
        {
            model.Effort ??= seeded.GetValueOrDefault(model.Id);
        }
        db.Settings.Add(new Setting { Key = EffortSeededSetting, Value = "true" });
        await db.SaveChangesAsync(ct);
    }

    private async Task<Seed> ReadSeedAsync(CancellationToken ct)
    {
        Seed seed;
        await using (var file = File.OpenRead(options.Value.SeedFile))
        {
            seed = await JsonSerializer.DeserializeAsync<Seed>(file, Json, ct)
                ?? throw new InvalidDataException($"Seed file '{options.Value.SeedFile}' is empty.");
        }
        foreach (var m in seed.Models)
        {
            Effort.Validate(m.Effort, m.Id);
            OpenRouterRoutingOptions.Validate(m.OpenRouter);
            ValidateConversations(m.MaxConversations, m.Id);
        }
        return seed;
    }

    /// <summary>A stored mapping that no longer reads is ignored at request time; say which model lost it.</summary>
    private async Task WarnUnreadableEffortAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.Database.SqlQuery<StoredEffort>($"SELECT Id, Effort FROM Models WHERE Effort IS NOT NULL").ToListAsync(ct);
        foreach (var row in stored)
        {
            try
            {
                Effort.Validate(Effort.Deserialize(row.Effort) ?? throw new InvalidDataException("unreadable"), row.Id);
            }
            catch (InvalidDataException e)
            {
                logger.LogWarning("Model {model} has an invalid stored effort mapping, which is ignored where it does not apply: {reason}", row.Id, e.Message);
            }
        }
    }

    /// <summary>
    /// Overrides only the fields each entry sets, so real endpoints and keys never live in the committed seed. Applied
    /// only when the file's content changed since it was last applied, so edits made in the dashboard survive a restart.
    /// </summary>
    private async Task ApplyOverridesAsync(CancellationToken ct)
    {
        var path = options.Value.SeedOverridePath;
        if (!File.Exists(path))
        {
            return;
        }

        var content = await File.ReadAllBytesAsync(path, ct);
        var hash = Convert.ToHexString(SHA256.HashData(content));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var applied = await db.Settings.FindAsync([OverrideHashSetting], ct);
        if (applied?.Value == hash)
        {
            return;
        }

        var overrides = JsonSerializer.Deserialize<Overrides>(content.AsSpan().StartsWith(Utf8Bom) ? content.AsSpan(Utf8Bom.Length) : content, Json)
            ?? throw new InvalidDataException($"Seed override file '{path}' is empty.");
        foreach (var o in overrides.Models ?? [])
        {
            Effort.Validate(o.Effort, o.Id);
            OpenRouterRoutingOptions.Validate(o.OpenRouter);
            ValidateConversations(o.MaxConversations, o.Id);
            if (await db.Models.FindAsync([o.Id], ct) is not { } model)
            {
                logger.LogWarning("Seed override names unknown model {model}; skipped", o.Id);
                continue;
            }
            model.Enabled = o.Enabled ?? model.Enabled;
            model.BaseUrl = o.BaseUrl ?? model.BaseUrl;
            model.UpstreamModelId = o.UpstreamModelId ?? model.UpstreamModelId;
            model.ProtectedApiKey = o.ApiKey is null ? model.ProtectedApiKey : Protect(o.ApiKey);
            model.ContextWindow = o.ContextWindow ?? model.ContextWindow;
            model.MaxPromptTokens = o.MaxPromptTokens ?? model.MaxPromptTokens;
            model.MaxConversations = o.MaxConversations ?? model.MaxConversations;
            model.SupportsTools = o.SupportsTools ?? model.SupportsTools;
            model.SupportsVision = o.SupportsVision ?? model.SupportsVision;
            model.Effort = o.Effort ?? model.Effort;
            model.OpenRouter = o.OpenRouter ?? model.OpenRouter;
        }
        if (applied is null)
        {
            db.Settings.Add(new Setting { Key = OverrideHashSetting, Value = hash });
        }
        else
        {
            applied.Value = hash;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Applied the seed override file");
    }

    private static void ValidateConversations(int? maxConversations, string id)
    {
        if (maxConversations is <= 0)
        {
            throw new InvalidDataException($"Model {id}: maxConversations must be a positive number, or absent for no limit.");
        }
    }

    private string? Protect(string? apiKey) => string.IsNullOrEmpty(apiKey) ? null : secrets.Protect(apiKey);
}
