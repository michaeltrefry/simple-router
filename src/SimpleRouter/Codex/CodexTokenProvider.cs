namespace SimpleRouter.Codex;

/// <summary>Returns a valid Codex access token and account ID, refreshing and persisting them before expiry.</summary>
public sealed class CodexTokenProvider(CodexCredentialStore store, CodexOAuthClient oauth, TimeProvider time)
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <param name="rejected">An access token the backend refused; it is refreshed even if not yet expired.</param>
    public async Task<CodexAccess> GetAccessAsync(CancellationToken ct = default, string? rejected = null)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var tokens = await store.GetAsync(ct) ?? throw new CodexNotLoggedInException();
            if (tokens.AccessToken is not null && tokens.AccessToken != rejected && tokens.AccessTokenExpiresAt > time.GetUtcNow() + RefreshSkew)
            {
                return new CodexAccess(tokens.AccessToken, tokens.AccountId);
            }

            // The issuer rotates the refresh token, so once sent the exchange must finish and persist even if the
            // caller disconnects; the OAuth client timeout bounds it.
            var refreshed = await oauth.RefreshAsync(tokens, CancellationToken.None);
            await store.SaveAsync(refreshed, CancellationToken.None);
            return new CodexAccess(refreshed.AccessToken!, refreshed.AccountId);
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
