using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleRouter.Data;

namespace SimpleRouter.Routing;

/// <summary>Loads the model catalog, tier membership, aliases and default tier from the seed file into an empty database.</summary>
public sealed class CatalogSeeder(IDbContextFactory<RouterDbContext> dbFactory, IOptions<RouterOptions> options)
{
    public const string DefaultTierSetting = "default_tier";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record SeedModel(string Id, string Provider, string UpstreamModelId, bool Enabled);

    private sealed record SeedAlias(string Pattern, string Tier);

    private sealed record Seed(string DefaultTier, SeedModel[] Models, Dictionary<string, string[]> Tiers, SeedAlias[] Aliases);

    /// <summary>Seeds once; later edits to the tables are kept across restarts.</summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Models.AnyAsync(ct))
        {
            return;
        }

        await using var file = File.OpenRead(options.Value.SeedFile);
        var seed = await JsonSerializer.DeserializeAsync<Seed>(file, Json, ct)
            ?? throw new InvalidDataException($"Seed file '{options.Value.SeedFile}' is empty.");

        db.Models.AddRange(seed.Models.Select(m => new CatalogModel
        {
            Id = m.Id,
            Provider = Enum.Parse<ProviderKind>(m.Provider, ignoreCase: true),
            UpstreamModelId = m.UpstreamModelId,
            Enabled = m.Enabled,
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
}
