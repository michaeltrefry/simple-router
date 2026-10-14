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

/// <summary>Local OpenAI-compatible endpoint settings.</summary>
public sealed class LocalOptions
{
    /// <summary>Seconds between endpoint health checks; 0 disables the periodic check.</summary>
    public int HealthCheckIntervalSeconds { get; set; } = 30;
}
