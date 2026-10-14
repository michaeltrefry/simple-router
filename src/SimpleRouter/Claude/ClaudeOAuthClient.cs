using System.Buffers.Text;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SimpleRouter.Claude;

/// <summary>A pending PKCE login: the URL to open plus the secrets needed to finish it.</summary>
public sealed record PkceLogin(string AuthorizeUrl, string State, string CodeVerifier);

/// <summary>Speaks the Claude subscription OAuth protocol (PKCE authorize, code exchange, refresh).</summary>
public sealed class ClaudeOAuthClient(IHttpClientFactory httpClientFactory, IOptions<RouterOptions> options, TimeProvider time)
{
    public const string HttpClientName = "claude-oauth";

    // The token endpoint rate-limits generic client user agents.
    private const string UserAgent = "simple-router/1.0";
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    private ClaudeOptions Claude => options.Value.Claude;

    public PkceLogin StartLogin()
    {
        var verifier = RandomToken();
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomToken();
        var query = new Dictionary<string, string>
        {
            ["code"] = "true",
            ["client_id"] = Claude.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = Claude.RedirectUri,
            ["scope"] = Claude.Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        var url = Claude.AuthorizeUrl + "?" + string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new PkceLogin(url, state, verifier);
    }

    public Task<ClaudeTokens> ExchangeCodeAsync(string code, PkceLogin login, CancellationToken ct = default) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["state"] = login.State,
            ["client_id"] = Claude.ClientId,
            ["redirect_uri"] = Claude.RedirectUri,
            ["code_verifier"] = login.CodeVerifier,
        }, fallbackRefreshToken: null, ct);

    public Task<ClaudeTokens> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = Claude.ClientId,
        }, fallbackRefreshToken: refreshToken, ct);

    private async Task<ClaudeTokens> PostTokenAsync(Dictionary<string, string> body, string? fallbackRefreshToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Claude.TokenUrl) { Content = JsonContent.Create(body) };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ClaudeOAuthException((int)response.StatusCode,
                $"Claude token request failed with status {(int)response.StatusCode}.");
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        var refresh = string.IsNullOrEmpty(token?.RefreshToken) ? fallbackRefreshToken : token.RefreshToken;
        if (string.IsNullOrEmpty(token?.AccessToken) || string.IsNullOrEmpty(refresh))
        {
            throw new ClaudeOAuthException((int)response.StatusCode, "Claude token response omitted required credentials.");
        }
        var lifetime = token.ExpiresIn > 0 ? TimeSpan.FromSeconds(token.ExpiresIn) : DefaultLifetime;
        return new ClaudeTokens(refresh, token.AccessToken, time.GetUtcNow() + lifetime);
    }

    private static string RandomToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] long ExpiresIn);
}
