using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.Claude;

/// <summary>Persists the Claude subscription tokens encrypted at rest.</summary>
public sealed class ClaudeCredentialStore(IDbContextFactory<RouterDbContext> dbFactory, SecretProtector protector)
{
    private const int CredentialId = 1;

    public async Task<ClaudeTokens?> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ClaudeCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == CredentialId, ct);
        if (row is null)
        {
            return null;
        }
        var access = row.ProtectedAccessToken is null ? null : protector.Unprotect(row.ProtectedAccessToken);
        return new ClaudeTokens(protector.Unprotect(row.ProtectedRefreshToken), access, row.AccessTokenExpiresAt);
    }

    public Task SaveAsync(ClaudeTokens tokens, CancellationToken ct = default) => SaveAsync(tokens, false, ct);

    /// <summary>Saves an explicit login and discards the previous account's cooldowns in the same transaction.</summary>
    public Task SaveLoginAsync(ClaudeTokens tokens, CancellationToken ct = default) => SaveAsync(tokens, true, ct);

    private async Task SaveAsync(ClaudeTokens tokens, bool resetCooldowns, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ClaudeCredentials.SingleOrDefaultAsync(c => c.Id == CredentialId, ct);
        if (row is null)
        {
            row = new ClaudeCredential { Id = CredentialId, ProtectedRefreshToken = "" };
            db.ClaudeCredentials.Add(row);
        }
        row.ProtectedRefreshToken = protector.Protect(tokens.RefreshToken);
        row.ProtectedAccessToken = tokens.AccessToken is null ? null : protector.Protect(tokens.AccessToken);
        row.AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
        if (resetCooldowns)
        {
            db.ModelCooldowns.RemoveRange(await db.ModelCooldowns
                .Where(c => db.Models.Any(m => m.Id == c.ModelId && m.Provider == ProviderKind.Claude)).ToListAsync(ct));
        }
        await db.SaveChangesAsync(ct);
    }
}
