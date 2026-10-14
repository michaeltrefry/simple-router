using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SimpleRouter.Routing;

namespace SimpleRouter.Data;

public sealed class RouterDbContext(DbContextOptions<RouterDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<ClaudeCredential> ClaudeCredentials => Set<ClaudeCredential>();
    public DbSet<CodexCredential> CodexCredentials => Set<CodexCredential>();
    public DbSet<CatalogModel> Models => Set<CatalogModel>();
    public DbSet<TierMember> TierMembers => Set<TierMember>();
    public DbSet<TierAlias> TierAliases => Set<TierAlias>();
    public DbSet<TierCursor> TierCursors => Set<TierCursor>();
    public DbSet<SessionPin> SessionPins => Set<SessionPin>();
    public DbSet<ProviderModelList> ProviderModelLists => Set<ProviderModelList>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Setting>().HasKey(s => s.Key);
        modelBuilder.Entity<ClaudeCredential>().Property(c => c.Id).ValueGeneratedNever();
        modelBuilder.Entity<CodexCredential>().Property(c => c.Id).ValueGeneratedNever();

        modelBuilder.Entity<CatalogModel>().HasKey(m => m.Id);
        modelBuilder.Entity<CatalogModel>().Property(m => m.Provider).HasConversion(p => p.ToString().ToLowerInvariant(), p => Enum.Parse<ProviderKind>(p, true));
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
