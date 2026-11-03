using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;

namespace SimpleRouter.Routing;

/// <summary>One protected key per paid provider; key changes clear only that provider's stale state.</summary>
public abstract class ApiKeyCredentialStore(
    IDbContextFactory<RouterDbContext> dbFactory, SecretProtector protector, ProviderKind provider)
{
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ProviderCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.Provider == provider, ct);
        return row is null ? null : protector.Unprotect(row.ProtectedApiKey);
    }

    public async Task<bool> HasKeyAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ProviderCredentials.AnyAsync(c => c.Provider == provider, ct);
    }

    public async Task SetAsync(string? apiKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ProviderCredentials.FindAsync([provider], ct);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (row is not null) db.ProviderCredentials.Remove(row);
        }
        else
        {
            if (row is null)
            {
                row = new ProviderCredential { Provider = provider, ProtectedApiKey = "" };
                db.ProviderCredentials.Add(row);
            }
            row.ProtectedApiKey = protector.Protect(apiKey.Trim());
        }
        db.ModelCooldowns.RemoveRange(await db.ModelCooldowns.Where(c => db.Models.Any(m => m.Id == c.ModelId && m.Provider == provider)).ToListAsync(ct));
        db.ProviderModelLists.RemoveRange(await db.ProviderModelLists.Where(l => l.Provider == provider).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
