using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.OpenRouter;

/// <summary>Stores one protected API key shared by all OpenRouter catalog entries.</summary>
public sealed class OpenRouterCredentialStore(IDbContextFactory<RouterDbContext> dbFactory, SecretProtector protector)
{
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ProviderCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.Provider == ProviderKind.OpenRouter, ct);
        return row is null ? null : protector.Unprotect(row.ProtectedApiKey);
    }

    public async Task<bool> HasKeyAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ProviderCredentials.AnyAsync(c => c.Provider == ProviderKind.OpenRouter, ct);
    }

    /// <summary>Null or whitespace removes the key; replacing it clears stale account cooldowns and discovery.</summary>
    public async Task SetAsync(string? apiKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ProviderCredentials.FindAsync([ProviderKind.OpenRouter], ct);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (row is not null) db.ProviderCredentials.Remove(row);
        }
        else
        {
            if (row is null)
            {
                row = new ProviderCredential { Provider = ProviderKind.OpenRouter, ProtectedApiKey = "" };
                db.ProviderCredentials.Add(row);
            }
            row.ProtectedApiKey = protector.Protect(apiKey.Trim());
        }
        db.ModelCooldowns.RemoveRange(await db.ModelCooldowns.Where(c => db.Models.Any(m => m.Id == c.ModelId && m.Provider == ProviderKind.OpenRouter)).ToListAsync(ct));
        db.ProviderModelLists.RemoveRange(await db.ProviderModelLists.Where(l => l.Provider == ProviderKind.OpenRouter).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
