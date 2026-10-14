namespace SimpleRouter.Codex;

/// <summary>Plaintext Codex subscription tokens and the ChatGPT account they act for; only ever held in memory.</summary>
public sealed record CodexTokens(string RefreshToken, string? AccessToken, DateTimeOffset? AccessTokenExpiresAt, string AccountId);

/// <summary>An access token plus the account ID the Codex backend requires alongside it.</summary>
public sealed record CodexAccess(string AccessToken, string AccountId);

/// <summary>No Codex subscription has been stored yet.</summary>
public sealed class CodexNotLoggedInException()
    : Exception("No Codex subscription is configured; run 'simple-router login codex'.");

/// <summary>The Codex auth issuer rejected a request. The body is not retained since it may hold credentials.</summary>
public sealed class CodexOAuthException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
