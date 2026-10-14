namespace SimpleRouter;

/// <summary>Configuration bound from the <c>SimpleRouter</c> section.</summary>
public sealed class RouterOptions
{
    public const string Section = "SimpleRouter";

    public static readonly string[] DefaultModels = ["claude-sonnet-4-5"];

    /// <summary>Loopback port the router listens on. 0 picks a free port.</summary>
    public int Port { get; set; } = 8790;

    /// <summary>Holds the SQLite database and the Data Protection key ring.</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".simple-router");

    /// <summary>Model IDs advertised by <c>GET /v1/models</c>.</summary>
    public string[] Models { get; set; } = [];

    /// <summary>Catalog, tier membership, aliases and default tier loaded into an empty database on first run.</summary>
    public string SeedFile { get; set; } = Path.Combine(AppContext.BaseDirectory, "seed.json");

    /// <summary>
    /// Optional per-machine model overrides (endpoints, keys, enablement) applied on every start; defaults to
    /// <c>seed.local.json</c> in <see cref="DataDirectory"/>.
    /// </summary>
    public string? SeedOverrideFile { get; set; }

    public ClaudeOptions Claude { get; set; } = new();

    public CodexOptions Codex { get; set; } = new();

    public LocalOptions Local { get; set; } = new();

    public string SeedOverridePath => string.IsNullOrWhiteSpace(SeedOverrideFile) ? Path.Combine(DataDirectory, "seed.local.json") : SeedOverrideFile;

    public IReadOnlyList<string> ModelIds => Models.Length > 0 ? Models : DefaultModels;
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
}
