using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SimpleRouter.Routing;
using SimpleRouter.OpenRouter;

namespace SimpleRouter.Data;

public sealed class RouterDbContext(DbContextOptions<RouterDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<ClaudeCredential> ClaudeCredentials => Set<ClaudeCredential>();
    public DbSet<CodexCredential> CodexCredentials => Set<CodexCredential>();
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();
    public DbSet<CatalogModel> Models => Set<CatalogModel>();
    public DbSet<TierMember> TierMembers => Set<TierMember>();
    public DbSet<TierAlias> TierAliases => Set<TierAlias>();
    public DbSet<TierCursor> TierCursors => Set<TierCursor>();
    public DbSet<SessionPin> SessionPins => Set<SessionPin>();
    public DbSet<ProviderModelList> ProviderModelLists => Set<ProviderModelList>();
    public DbSet<ModelCooldown> ModelCooldowns => Set<ModelCooldown>();
    public DbSet<ModelPrice> ModelPrices => Set<ModelPrice>();
    public DbSet<RequestUsage> RequestUsages => Set<RequestUsage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Setting>().HasKey(s => s.Key);
        modelBuilder.Entity<ClaudeCredential>().Property(c => c.Id).ValueGeneratedNever();
        modelBuilder.Entity<CodexCredential>().Property(c => c.Id).ValueGeneratedNever();

        modelBuilder.Entity<CatalogModel>().HasKey(m => m.Id);
        modelBuilder.Entity<CatalogModel>().Property(m => m.Provider).HasConversion(p => p.ToString().ToLowerInvariant(), p => Enum.Parse<ProviderKind>(p, true));
        // Stored as seed-shaped JSON and compared by it, so a mapping edited in place is still saved.
        modelBuilder.Entity<CatalogModel>().Property(m => m.Effort).HasConversion(
            e => e == null ? null : JsonSerializer.Serialize(e, Effort.Json),
            e => e == null ? null : Effort.Deserialize(e),
            new ValueComparer<EffortMapping?>(
                (a, b) => JsonSerializer.Serialize(a, Effort.Json) == JsonSerializer.Serialize(b, Effort.Json),
                e => JsonSerializer.Serialize(e, Effort.Json).GetHashCode(),
                e => e == null ? null : JsonSerializer.Deserialize<EffortMapping>(JsonSerializer.Serialize(e, Effort.Json), Effort.Json)));
        modelBuilder.Entity<ProviderCredential>().HasKey(c => c.Provider);
        modelBuilder.Entity<ProviderCredential>().Property(c => c.Provider).HasConversion(p => p.ToString().ToLowerInvariant(), p => Enum.Parse<ProviderKind>(p, true));
        modelBuilder.Entity<CatalogModel>().Property(m => m.OpenRouter).HasConversion(
            o => o == null ? null : JsonSerializer.Serialize(o, OpenRouterRoutingOptions.Json),
            o => o == null ? null : JsonSerializer.Deserialize<OpenRouterRoutingOptions>(o, OpenRouterRoutingOptions.Json),
            new ValueComparer<OpenRouterRoutingOptions?>(
                (a, b) => JsonSerializer.Serialize(a, OpenRouterRoutingOptions.Json) == JsonSerializer.Serialize(b, OpenRouterRoutingOptions.Json),
                o => JsonSerializer.Serialize(o, OpenRouterRoutingOptions.Json).GetHashCode(),
                o => o == null ? null : JsonSerializer.Deserialize<OpenRouterRoutingOptions>(JsonSerializer.Serialize(o, OpenRouterRoutingOptions.Json), OpenRouterRoutingOptions.Json)));
        modelBuilder.Entity<TierMember>().HasKey(m => new { m.Tier, m.ModelId });
        modelBuilder.Entity<TierMember>().Property(m => m.Tier).HasConversion(TierConverter);
        modelBuilder.Entity<TierMember>().HasOne<CatalogModel>().WithMany().HasForeignKey(m => m.ModelId);
        modelBuilder.Entity<TierAlias>().HasKey(a => a.Pattern);
        modelBuilder.Entity<TierAlias>().Property(a => a.Tier).HasConversion(TierConverter);
        modelBuilder.Entity<TierCursor>().HasKey(c => c.Tier);
        modelBuilder.Entity<TierCursor>().Property(c => c.Tier).HasConversion(TierConverter);
        modelBuilder.Entity<SessionPin>().HasKey(p => new { p.SessionKey, p.Tier });
        modelBuilder.Entity<SessionPin>().Property(p => p.Tier).HasConversion(TierConverter);
        modelBuilder.Entity<SessionPin>().HasIndex(p => p.ClientSessionId);
        modelBuilder.Entity<ProviderModelList>().HasKey(l => l.Provider);
        modelBuilder.Entity<ProviderModelList>().Property(l => l.Provider).HasConversion(p => p.ToString().ToLowerInvariant(), p => Enum.Parse<ProviderKind>(p, true));
        modelBuilder.Entity<ModelCooldown>().HasKey(c => c.ModelId);
        modelBuilder.Entity<ModelCooldown>().Property(c => c.Reason).HasConversion<string>();
        modelBuilder.Entity<ModelPrice>().HasKey(p => p.ModelId);
        modelBuilder.Entity<RequestUsage>().Property(u => u.Provider).HasConversion(p => p.ToString().ToLowerInvariant(), p => Enum.Parse<ProviderKind>(p, true));
        modelBuilder.Entity<RequestUsage>().Property(u => u.Tier).HasConversion(TierConverter);
        modelBuilder.Entity<RequestUsage>().HasIndex(u => u.ClientSessionId);
    }

    private static readonly ValueConverter<ModelTier, string> TierConverter =
        new(t => t.Name(), t => ModelTiers.Parse(t));
}

/// <summary>A key/value row; secret values are stored protected.</summary>
public sealed class Setting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

/// <summary>The single Claude subscription credential; token columns are protected.</summary>
public sealed class ClaudeCredential
{
    public int Id { get; set; }
    public required string ProtectedRefreshToken { get; set; }
    public string? ProtectedAccessToken { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
}

/// <summary>The single Codex subscription credential; token and account columns are protected.</summary>
public sealed class CodexCredential
{
    public int Id { get; set; }
    public required string ProtectedRefreshToken { get; set; }
    public string? ProtectedAccessToken { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public required string ProtectedAccountId { get; set; }
}
