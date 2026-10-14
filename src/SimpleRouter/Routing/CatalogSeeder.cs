using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleRouter.Data;

namespace SimpleRouter.Routing;

/// <summary>
/// Loads the model catalog, tier membership, aliases and default tier from the seed file into an empty database, then
/// applies the optional per-machine override file to existing models on every start.
/// </summary>
public sealed class CatalogSeeder(
    IDbContextFactory<RouterDbContext> dbFactory,
    IOptions<RouterOptions> options,
    SecretProtector secrets,
    ILogger<CatalogSeeder> logger)
{
    public const string DefaultTierSetting = "default_tier";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record SeedModel(
        string Id,
        string Provider,
        string UpstreamModelId,
        bool Enabled,
        string? BaseUrl = null,
        string? ApiKey = null,
        int? ContextWindow = null,
        bool SupportsTools = true,
        bool SupportsVision = false);

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
        bool? SupportsTools,
        bool? SupportsVision);

    private sealed record Overrides(ModelOverride[]? Models);

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedEmptyAsync(ct);
        await SeedPricesAsync(ct);
        await ApplyOverridesAsync(ct);
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

    private async Task<Seed> ReadSeedAsync(CancellationToken ct)
    {
        await using var file = File.OpenRead(options.Value.SeedFile);
        return await JsonSerializer.DeserializeAsync<Seed>(file, Json, ct)
            ?? throw new InvalidDataException($"Seed file '{options.Value.SeedFile}' is empty.");
    }

    /// <summary>Seeds once; later edits to the tables are kept across restarts.</summary>
    private async Task SeedEmptyAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Models.AnyAsync(ct))
        {
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
            SupportsTools = m.SupportsTools,
            SupportsVision = m.SupportsVision,
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
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Overrides only the fields each entry sets, so real endpoints and keys never live in the committed seed.</summary>
    private async Task ApplyOverridesAsync(CancellationToken ct)
    {
        var path = options.Value.SeedOverridePath;
        if (!File.Exists(path))
        {
            return;
        }

        Overrides overrides;
        await using (var file = File.OpenRead(path))
        {
            overrides = await JsonSerializer.DeserializeAsync<Overrides>(file, Json, ct)
                ?? throw new InvalidDataException($"Seed override file '{path}' is empty.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var o in overrides.Models ?? [])
        {
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
            model.SupportsTools = o.SupportsTools ?? model.SupportsTools;
            model.SupportsVision = o.SupportsVision ?? model.SupportsVision;
        }
        await db.SaveChangesAsync(ct);
    }

    private string? Protect(string? apiKey) => string.IsNullOrEmpty(apiKey) ? null : secrets.Protect(apiKey);
}
