using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;

namespace SimpleRouter.Gateway;

/// <summary>Owns the router-generated gateway key clients must present.</summary>
public sealed class GatewayKeyStore(IDbContextFactory<RouterDbContext> dbFactory, SecretProtector protector)
{
    private const string SettingKey = "gateway_key";
    private const string KeyPrefix = "sr_";
    private string? _cached;

    /// <summary>Generates and persists the key if none exists. Returns true when one was created.</summary>
    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.FindAsync([SettingKey], ct);
        if (row is not null)
        {
            _cached = protector.Unprotect(row.Value);
            return false;
        }

        var key = NewKey();
        db.Settings.Add(new Setting { Key = SettingKey, Value = protector.Protect(key) });
        await db.SaveChangesAsync(ct);
        _cached = key;
        return true;
    }

    /// <summary>Replaces the key; the old one is rejected from the next request on. Returns the new key.</summary>
    public async Task<string> RegenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = NewKey();
        var row = await db.Settings.FindAsync([SettingKey], ct);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = SettingKey, Value = protector.Protect(key) });
        }
        else
        {
            row.Value = protector.Protect(key);
        }
        await db.SaveChangesAsync(ct);
        _cached = key;
        return key;
    }

    private static string NewKey() => KeyPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        if (_cached is null)
        {
            await EnsureAsync(ct);
        }
        return _cached!;
    }
}
