using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SimpleRouter.Codex;

/// <summary>A started device authorization: what the user enters, and how often to poll.</summary>
public sealed record DeviceCode(string DeviceAuthId, string UserCode, TimeSpan Interval, string VerificationUrl);

/// <summary>The device authorization the user approved, ready to exchange for tokens.</summary>
public sealed record DeviceAuthorization(string AuthorizationCode, string CodeVerifier);

/// <summary>Speaks the OpenAI device-code flow used by the Codex CLI, plus code exchange and refresh.</summary>
public sealed class CodexOAuthClient(IHttpClientFactory httpClientFactory, IOptions<RouterOptions> options, TimeProvider time)
{
    public const string HttpClientName = "codex-oauth";

    private const string UserAgent = "simple-router/1.0";
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    private CodexOptions Codex => options.Value.Codex;
    private string Issuer => Codex.Issuer.TrimEnd('/');

    public async Task<DeviceCode> StartDeviceAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(Issuer + "/api/accounts/deviceauth/usercode", JsonContent.Create(new { client_id = Codex.ClientId }), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new CodexOAuthException((int)response.StatusCode, $"Codex device authorization failed with status {(int)response.StatusCode}.");
        }
        var body = await ReadObjectAsync(response, ct);
        var deviceAuthId = Str(body["device_auth_id"]);
        var userCode = Str(body["user_code"]) ?? Str(body["usercode"]);
        if (string.IsNullOrEmpty(deviceAuthId) || string.IsNullOrEmpty(userCode))
        {
            throw new CodexOAuthException((int)response.StatusCode, "Codex device authorization returned an invalid response.");
        }
        return new DeviceCode(deviceAuthId, userCode, Interval(body["interval"]), Issuer + "/codex/device");
    }

    /// <returns>The approved authorization, or null while the user has not entered the code yet.</returns>
    public async Task<DeviceAuthorization?> PollDeviceAsync(DeviceCode device, CancellationToken ct = default)
    {
        using var response = await SendAsync(Issuer + "/api/accounts/deviceauth/token",
            JsonContent.Create(new { device_auth_id = device.DeviceAuthId, user_code = device.UserCode }), ct);
        // The issuer answers 403/404 until the code is entered, like the Codex CLI expects.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new CodexOAuthException((int)response.StatusCode, "Codex device authorization was denied or has expired.");
        }
        var body = await ReadObjectAsync(response, ct);
        var code = Str(body["authorization_code"]);
        var verifier = Str(body["code_verifier"]);
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(verifier))
        {
            throw new CodexOAuthException((int)response.StatusCode, "Codex device authorization returned an invalid response.");
        }
        return new DeviceAuthorization(code, verifier);
    }

    public Task<CodexTokens> ExchangeAsync(DeviceAuthorization authorization, CancellationToken ct = default) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorization.AuthorizationCode,
            ["redirect_uri"] = Issuer + "/deviceauth/callback",
            ["client_id"] = Codex.ClientId,
            ["code_verifier"] = authorization.CodeVerifier,
        }, previous: null, ct);

    public Task<CodexTokens> RefreshAsync(CodexTokens previous, CancellationToken ct = default) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = previous.RefreshToken,
            ["client_id"] = Codex.ClientId,
        }, previous, ct);

    private async Task<CodexTokens> PostTokenAsync(Dictionary<string, string> form, CodexTokens? previous, CancellationToken ct)
    {
        using var response = await SendAsync(Codex.TokenUrl, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new CodexOAuthException((int)response.StatusCode, $"Codex token request failed with status {(int)response.StatusCode}.");
        }
        var body = await ReadObjectAsync(response, ct);
        var access = Str(body["access_token"]);
        var refresh = Str(body["refresh_token"]) is { Length: > 0 } rotated ? rotated : previous?.RefreshToken;
        var accountId = AccountId(Str(body["id_token"])) ?? AccountId(access) ?? previous?.AccountId;
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(accountId))
        {
            throw new CodexOAuthException((int)response.StatusCode, "Codex token response omitted required credentials.");
        }
        var expiresAt = body["expires_in"] is JsonValue e && e.TryGetValue<long>(out var seconds) && seconds > 0
            ? time.GetUtcNow() + TimeSpan.FromSeconds(seconds)
            : Expiry(access) ?? time.GetUtcNow() + DefaultLifetime;
        return new CodexTokens(refresh, access, expiresAt, accountId);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, HttpContent content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        try
        {
            return await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new CodexOAuthException(0, "Codex auth endpoint is unreachable.");
        }
    }

    private static async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct) is JsonObject body)
            {
                return body;
            }
        }
        catch (JsonException)
        {
        }
        throw new CodexOAuthException((int)response.StatusCode, "Codex auth response was malformed.");
    }

    /// <summary>The issuer sends the interval as a string; clamp it so a bad value never hammers or stalls the poll.</summary>
    private static TimeSpan Interval(JsonNode? node)
    {
        var seconds = node switch
        {
            JsonValue v when v.TryGetValue<int>(out var n) => n,
            JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, out var n) => n,
            _ => -1,
        };
        return seconds < 1 ? DefaultInterval : TimeSpan.FromSeconds(Math.Min(seconds, MaxInterval.TotalSeconds));
    }

    /// <summary>The ChatGPT account ID from an id or access token's claims, as the Codex CLI reads it.</summary>
    public static string? AccountId(string? jwt)
    {
        var claims = Claims(jwt);
        return Str(claims?["https://api.openai.com/auth"]?["chatgpt_account_id"])
            ?? Str(claims?["chatgpt_account_id"])
            ?? Str((claims?["organizations"] as JsonArray)?.FirstOrDefault()?["id"]);
    }

    private static DateTimeOffset? Expiry(string? jwt) =>
        Claims(jwt)?["exp"] is JsonValue v && v.TryGetValue<long>(out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp) : null;

    private static JsonObject? Claims(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(Base64Url.DecodeFromChars(parts[1])) as JsonObject;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
