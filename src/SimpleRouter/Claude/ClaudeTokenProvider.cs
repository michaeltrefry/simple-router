namespace SimpleRouter.Claude;

/// <summary>Returns a valid Claude access token, refreshing and persisting it before expiry.</summary>
public sealed class ClaudeTokenProvider(ClaudeCredentialStore store, ClaudeOAuthClient oauth, TimeProvider time)
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var tokens = await store.GetAsync(ct) ?? throw new ClaudeNotLoggedInException();
            if (tokens.AccessToken is not null && tokens.AccessTokenExpiresAt > time.GetUtcNow() + RefreshSkew)
            {
                return tokens.AccessToken;
            }

            var refreshed = await oauth.RefreshAsync(tokens.RefreshToken, ct);
            await store.SaveAsync(refreshed, ct);
            return refreshed.AccessToken!;
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
