namespace SimpleRouter.Claude;

/// <summary>Plaintext Claude subscription tokens; only ever held in memory.</summary>
public sealed record ClaudeTokens(string RefreshToken, string? AccessToken, DateTimeOffset? AccessTokenExpiresAt);

/// <summary>No Claude subscription has been stored yet.</summary>
public sealed class ClaudeNotLoggedInException()
    : Exception("No Claude subscription is configured; run 'simple-router login claude'.");

/// <summary>The Claude token endpoint rejected a request. The body is not retained since it may hold credentials.</summary>
public sealed class ClaudeOAuthException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
