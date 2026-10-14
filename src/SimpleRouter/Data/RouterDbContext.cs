using Microsoft.EntityFrameworkCore;

namespace SimpleRouter.Data;

public sealed class RouterDbContext(DbContextOptions<RouterDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<ClaudeCredential> ClaudeCredentials => Set<ClaudeCredential>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Setting>().HasKey(s => s.Key);
        modelBuilder.Entity<ClaudeCredential>().Property(c => c.Id).ValueGeneratedNever();
    }
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
