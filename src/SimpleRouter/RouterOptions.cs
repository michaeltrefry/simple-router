using System.Net;
using System.Net.Sockets;

namespace SimpleRouter;

/// <summary>Configuration bound from the <c>SimpleRouter</c> section.</summary>
public sealed class RouterOptions
{
    public const string Section = "SimpleRouter";

    /// <summary>Port the router listens on. 0 picks a free port.</summary>
    public int Port { get; set; } = 8790;

    /// <summary>
    /// IP address the router listens on: loopback by default; <c>0.0.0.0</c>, <c>::</c> or a LAN IP lets other machines
    /// reach <c>/v1</c>. The dashboard answers only connections from this machine whatever the bind.
    /// </summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Port for HTTPS on <see cref="ListenAddress"/>, served only when a certificate exists (<c>cert create</c> or
    /// <see cref="HttpsOptions.CertificatePath"/>); plain HTTP then stays on loopback. 0 picks a free port.
    /// </summary>
    public int HttpsPort { get; set; } = 8791;

    public HttpsOptions Https { get; set; } = new();

    /// <summary>Holds the SQLite database and the Data Protection key ring.</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".simple-router");

    /// <summary>Catalog, tier membership, aliases and default tier loaded into an empty database on first run.</summary>
    public string SeedFile { get; set; } = Path.Combine(AppContext.BaseDirectory, "seed.json");

    /// <summary>
    /// Optional per-machine model overrides (endpoints, keys, enablement) applied on every start; defaults to
    /// <c>seed.local.json</c> in <see cref="DataDirectory"/>.
    /// </summary>
    public string? SeedOverrideFile { get; set; }

    /// <summary>How often subscription model lists are re-fetched while the server runs; zero disables the periodic refresh.</summary>
    public TimeSpan ModelListRefreshInterval { get; set; } = TimeSpan.FromHours(6);

    public ClaudeOptions Claude { get; set; } = new();

    public CodexOptions Codex { get; set; } = new();

    public OpenRouterOptions OpenRouter { get; set; } = new();

    public LocalOptions Local { get; set; } = new();

    public LimitOptions Limits { get; set; } = new();

    public SystemOneOptions SystemOne { get; set; } = new();

    /// <summary>Parses <see cref="ListenAddress"/>; throws when it is not an IP address.</summary>
    public IPAddress ResolveListenAddress()
    {
        var text = ListenAddress?.Trim() ?? "";
        // TryParse also accepts shorthand IPv4 ("192.168.1" is 192.168.0.1), which is almost always a typo here.
        if (IPAddress.TryParse(text, out var address) && (address.AddressFamily != AddressFamily.InterNetwork || text.Count(c => c == '.') == 3))
        {
            return address;
        }
        throw new InvalidOperationException(
            $"{Section}:ListenAddress '{ListenAddress}' is not an IP address; use 127.0.0.1 (this machine only), 0.0.0.0, :: or one of this machine's LAN IPs.");
    }

    public string SeedOverridePath => string.IsNullOrWhiteSpace(SeedOverrideFile) ? Path.Combine(DataDirectory, "seed.local.json") : SeedOverrideFile;
}

/// <summary>A certificate the user supplies instead of the one <c>cert create</c> generates.</summary>
public sealed class HttpsOptions
{
    /// <summary>A PKCS#12 (<c>.pfx</c>/<c>.p12</c>) file holding the certificate and its private key.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>The PKCS#12 file's password, if it has one.</summary>
    public string? Password { get; set; }
}

/// <summary>Claude subscription endpoints; overridable so tests can point at fakes.</summary>
public sealed class ClaudeOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.anthropic.com";
    public string AuthorizeUrl { get; set; } = "https://claude.ai/oauth/authorize";
    public string TokenUrl { get; set; } = "https://console.anthropic.com/v1/oauth/token";
    public string ClientId { get; set; } = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public string RedirectUri { get; set; } = "https://console.anthropic.com/oauth/code/callback";
    public string Scope { get; set; } = "org:create_api_key user:profile user:inference";
}

/// <summary>Codex (ChatGPT) subscription endpoints; overridable so tests can point at fakes.</summary>
public sealed class CodexOptions
{
    /// <summary>OAuth issuer serving the device-code flow and the token endpoint.</summary>
    public string Issuer { get; set; } = "https://auth.openai.com";

    /// <summary>The Codex CLI's public client ID.</summary>
    public string ClientId { get; set; } = "app_EMoamEEZ73f0CkXaXp7hrann";

    /// <summary>Responses API base; requests go to <c>{ApiBaseUrl}/responses</c>.</summary>
    public string ApiBaseUrl { get; set; } = "https://chatgpt.com/backend-api/codex";

    /// <summary>
    /// Sent as <c>client_version</c> on the models request. The backend hides models that need a newer client, so the
    /// default sits far ahead of real releases instead of pinning one that new models soon outgrow.
    /// </summary>
    public string ClientVersion { get; set; } = "99.0.0";

    /// <summary>How long <c>login codex</c> waits for the code to be entered.</summary>
    public TimeSpan DeviceCodeTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>A stream that sends nothing for this long is ended with an error event.</summary>
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(300);

    public string TokenUrl => Issuer.TrimEnd('/') + "/oauth/token";
}

/// <summary>Local OpenAI-compatible endpoint settings.</summary>
public sealed class LocalOptions
{
    /// <summary>Seconds between endpoint health checks after the one at startup; 0 disables the periodic check.</summary>
    public int HealthCheckIntervalSeconds { get; set; } = 30;

    /// <summary>A streaming response that sends nothing for this long is ended with an error event.</summary>
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long after its last request ends a conversation still holds its place on a local model with
    /// <see cref="Data.CatalogModel.MaxConversations"/>; a request in flight always holds it.
    /// </summary>
    public TimeSpan ConversationIdle { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>SystemOne decision endpoint settings.</summary>
public sealed class SystemOneOptions
{
    /// <summary>A decision call that has not answered after this many seconds is 504; generous because a model may load on demand.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>The largest <c>/v1/systemone</c> body accepted; images and videos arrive base64-encoded inside it.</summary>
    public long MaxBodyBytes { get; set; } = 100 * 1024 * 1024;
}

/// <summary>How long a model is skipped after a fault, when the provider reports no reset, and how long failover stays possible on a stream.</summary>
public sealed class LimitOptions
{
    /// <summary>
    /// How long a translated (Codex or local) stream withholds <c>message_start</c> waiting for its first content or
    /// terminal event; after it the start is sent and an error can no longer move the request to another model.
    /// </summary>
    public TimeSpan StreamStartGrace { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// After <c>message_start</c>, a translated stream that has written nothing to the client for this long sends an
    /// Anthropic <c>ping</c>, so a client that drops silent streams keeps waiting; zero disables pings.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>A usage limit or rate limit (429) without a reported reset.</summary>
    public TimeSpan LimitCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The provider refused the model for the plan; retried only after this long.</summary>
    public TimeSpan PlanRejectionCooldown { get; set; } = TimeSpan.FromHours(6);

    /// <summary>An overloaded or failing (5xx, 529) or unreachable subscription.</summary>
    public TimeSpan OverloadedCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The provider rejected the subscription account or credentials; applies to all of its models.</summary>
    public TimeSpan AuthRejectionCooldown { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>OpenRouter endpoints; overridable for tests.</summary>
public sealed class OpenRouterOptions
{
    public string ApiBaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(120);
}
