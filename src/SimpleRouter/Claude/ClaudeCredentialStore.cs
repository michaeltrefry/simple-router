using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;

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

    public async Task SaveAsync(ClaudeTokens tokens, CancellationToken ct = default)
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
        await db.SaveChangesAsync(ct);
    }
}
