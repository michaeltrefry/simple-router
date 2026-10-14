using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;

namespace SimpleRouter.Codex;

/// <summary>Persists the Codex subscription tokens and account ID encrypted at rest.</summary>
public sealed class CodexCredentialStore(IDbContextFactory<RouterDbContext> dbFactory, SecretProtector protector)
{
    private const int CredentialId = 1;

    public async Task<CodexTokens?> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.CodexCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == CredentialId, ct);
        if (row is null)
        {
            return null;
        }
        var access = row.ProtectedAccessToken is null ? null : protector.Unprotect(row.ProtectedAccessToken);
        return new CodexTokens(protector.Unprotect(row.ProtectedRefreshToken), access, row.AccessTokenExpiresAt, protector.Unprotect(row.ProtectedAccountId));
    }

    public async Task SaveAsync(CodexTokens tokens, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.CodexCredentials.SingleOrDefaultAsync(c => c.Id == CredentialId, ct);
        if (row is null)
        {
            row = new CodexCredential { Id = CredentialId, ProtectedRefreshToken = "", ProtectedAccountId = "" };
            db.CodexCredentials.Add(row);
        }
        row.ProtectedRefreshToken = protector.Protect(tokens.RefreshToken);
        row.ProtectedAccessToken = tokens.AccessToken is null ? null : protector.Protect(tokens.AccessToken);
        row.AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
        row.ProtectedAccountId = protector.Protect(tokens.AccountId);
        await db.SaveChangesAsync(ct);
    }
}
