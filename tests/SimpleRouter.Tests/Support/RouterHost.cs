using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Claude;
using SimpleRouter.Codex;
using SimpleRouter.Gateway;
using SimpleRouter.Local;

namespace SimpleRouter.Tests.Support;

/// <summary>A real router on a loopback port with fake Claude upstream and token endpoints.</summary>
public sealed class RouterHost : IAsyncDisposable
{
    public const string UpstreamBase = "http://claude-upstream.test";
    public const string TokenUrl = "http://claude-oauth.test/v1/oauth/token";
    public const string AuthorizeUrl = "http://claude-oauth.test/oauth/authorize";
    public const string CodexIssuer = "http://codex-auth.test";
    public const string CodexApiBase = "http://codex.test/backend-api/codex";

    private readonly WebApplication _app;

    private RouterHost(WebApplication app, string dataDirectory, FakeHandler upstream, FakeHandler oauth, FakeHandler local, FakeHandler codex, FakeHandler codexAuth)
    {
        Codex = codex;
        CodexAuth = codexAuth;
        _app = app;
        DataDirectory = dataDirectory;
        Upstream = upstream;
        OAuth = oauth;
        Local = local;
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public string DataDirectory { get; }
    public FakeHandler Upstream { get; }
    public FakeHandler OAuth { get; }
    /// <summary>Every local OpenAI-compatible endpoint (chat and health checks); route on the request URI.</summary>
    public FakeHandler Local { get; }
    public HttpClient Client { get; }
    /// <summary>The Codex Responses backend.</summary>
    public FakeHandler Codex { get; }
    /// <summary>The Codex auth issuer (device flow and token endpoint).</summary>
    public FakeHandler CodexAuth { get; }
    public IServiceProvider Services => _app.Services;

    public static string[] Args(string dataDirectory)
    {
        var args = new List<string>
        {
            "--SimpleRouter:Port=0",
            $"--SimpleRouter:DataDirectory={dataDirectory}",
            $"--SimpleRouter:Claude:ApiBaseUrl={UpstreamBase}",
            $"--SimpleRouter:Claude:TokenUrl={TokenUrl}",
            $"--SimpleRouter:Claude:AuthorizeUrl={AuthorizeUrl}",
            $"--SimpleRouter:Codex:Issuer={CodexIssuer}",
            $"--SimpleRouter:Codex:ApiBaseUrl={CodexApiBase}",
            // Only the startup probe runs; tests re-check explicitly so results never race the assertions.
            "--SimpleRouter:Local:HealthCheckIntervalSeconds=0",
            // Model lists are fetched only where a test asks, so no upstream sees an unexpected request.
            "--SimpleRouter:ModelListRefreshInterval=00:00:00",
        };
        return args.ToArray();
    }

    public static Action<IServiceCollection> FakeHttp(FakeHandler upstream, FakeHandler oauth, FakeHandler? local = null,
        FakeHandler? codex = null, FakeHandler? codexAuth = null) => services =>
    {
        services.AddHttpClient(CodexUpstream.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => codex ?? new FakeHandler());
        services.AddHttpClient(CodexOAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => codexAuth ?? new FakeHandler());
        services.AddHttpClient(ClaudeUpstream.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);
        services.AddHttpClient(ClaudeOAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => oauth);
        if (local is not null)
        {
            services.AddHttpClient(LocalUpstream.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => local);
            services.AddHttpClient(LocalHealth.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => local);
        }
    };

    public static string NewDataDirectory() =>
        Path.Combine(Path.GetTempPath(), "simple-router-tests", Guid.NewGuid().ToString("N"));

    public static Task<RouterHost> StartAsync() => StartAsync(_ => { });

    public static Task<RouterHost> StartAsync(Action<IServiceCollection> configureServices)
    {
        var dataDirectory = NewDataDirectory();
        return StartInAsync(dataDirectory, configureServices, Args(dataDirectory));
    }

    /// <summary>Starts on <paramref name="dataDirectory"/> (which may hold a previous host's state) with extra config args.</summary>
    public static Task<RouterHost> StartInAsync(string dataDirectory, params string[] extraArgs) =>
        StartInAsync(dataDirectory, new FakeHandler(), extraArgs);

    /// <summary>As above, with <paramref name="local"/> already answering the health probe the router runs before serving.</summary>
    public static Task<RouterHost> StartInAsync(string dataDirectory, FakeHandler local, params string[] extraArgs) =>
        StartInAsync(dataDirectory, _ => { }, [.. Args(dataDirectory), .. extraArgs], local);

    /// <summary>As above, with extra service registrations (a log capture, say).</summary>
    public static Task<RouterHost> StartInAsync(string dataDirectory, FakeHandler local, Action<IServiceCollection> configureServices, params string[] extraArgs) =>
        StartInAsync(dataDirectory, configureServices, [.. Args(dataDirectory), .. extraArgs], local);

    private static async Task<RouterHost> StartInAsync(string dataDirectory, Action<IServiceCollection> configureServices, string[] args, FakeHandler? local = null)
    {
        var upstream = new FakeHandler();
        var oauth = new FakeHandler();
        local ??= new FakeHandler();
        var codex = new FakeHandler();
        var codexAuth = new FakeHandler();
        var app = RouterApp.Build(args, services =>
        {
            FakeHttp(upstream, oauth, local, codex, codexAuth)(services);
            configureServices(services);
        });
        try
        {
            await RouterApp.InitializeAsync(app);
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDelete(dataDirectory);
            throw;
        }
        return new RouterHost(app, dataDirectory, upstream, oauth, local, codex, codexAuth);
    }

    /// <summary>When set, disposal stops the host but leaves the data directory for a restarted host.</summary>
    public bool KeepData { get; set; }

    public Task<string> GatewayKeyAsync() => Services.GetRequiredService<GatewayKeyStore>().GetAsync();

    public async Task<HttpClient> AuthorizedClientAsync()
    {
        Client.DefaultRequestHeaders.Add(GatewayAuthMiddleware.RouterKeyHeader, await GatewayKeyAsync());
        return Client;
    }

    public Task LoginAsync(string accessToken = "access-current", DateTimeOffset? expiresAt = null, string refreshToken = "refresh-current") =>
        Services.GetRequiredService<ClaudeCredentialStore>()
            .SaveAsync(new ClaudeTokens(refreshToken, accessToken, expiresAt ?? DateTimeOffset.UtcNow.AddHours(1)));

    public Task LoginCodexAsync(string accessToken = "codex-access-current", DateTimeOffset? expiresAt = null,
        string refreshToken = "codex-refresh-current", string accountId = "acct-test") =>
        Services.GetRequiredService<CodexCredentialStore>()
            .SaveAsync(new CodexTokens(refreshToken, accessToken, expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), accountId));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (KeepData)
        {
            return;
        }
        TryDelete(DataDirectory);
    }

    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }
}
