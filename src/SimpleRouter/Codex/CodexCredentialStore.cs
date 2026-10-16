using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;

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

    public Task SaveAsync(CodexTokens tokens, CancellationToken ct = default) => SaveAsync(tokens, false, ct);

    /// <summary>Saves an explicit login and discards the previous account's cooldowns in the same transaction.</summary>
    public Task SaveLoginAsync(CodexTokens tokens, CancellationToken ct = default) => SaveAsync(tokens, true, ct);

    private async Task SaveAsync(CodexTokens tokens, bool resetCooldowns, CancellationToken ct)
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
        if (resetCooldowns)
        {
            db.ModelCooldowns.RemoveRange(await db.ModelCooldowns
                .Where(c => db.Models.Any(m => m.Id == c.ModelId && m.Provider == ProviderKind.Codex)).ToListAsync(ct));
        }
        await db.SaveChangesAsync(ct);
    }
}
