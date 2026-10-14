using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Claude;
using SimpleRouter.Gateway;

namespace SimpleRouter.Tests.Support;

/// <summary>A real router on a loopback port with fake Claude upstream and token endpoints.</summary>
public sealed class RouterHost : IAsyncDisposable
{
    public const string UpstreamBase = "http://claude-upstream.test";
    public const string TokenUrl = "http://claude-oauth.test/v1/oauth/token";
    public const string AuthorizeUrl = "http://claude-oauth.test/oauth/authorize";

    private readonly WebApplication _app;

    private RouterHost(WebApplication app, string dataDirectory, FakeHandler upstream, FakeHandler oauth)
    {
        _app = app;
        DataDirectory = dataDirectory;
        Upstream = upstream;
        OAuth = oauth;
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public string DataDirectory { get; }
    public FakeHandler Upstream { get; }
    public FakeHandler OAuth { get; }
    public HttpClient Client { get; }
    public IServiceProvider Services => _app.Services;

    public static string[] Args(string dataDirectory, params string[] models)
    {
        var args = new List<string>
        {
            "--SimpleRouter:Port=0",
            $"--SimpleRouter:DataDirectory={dataDirectory}",
            $"--SimpleRouter:Claude:ApiBaseUrl={UpstreamBase}",
            $"--SimpleRouter:Claude:TokenUrl={TokenUrl}",
            $"--SimpleRouter:Claude:AuthorizeUrl={AuthorizeUrl}",
        };
        args.AddRange(models.Select((m, i) => $"--SimpleRouter:Models:{i}={m}"));
        return args.ToArray();
    }

    public static Action<IServiceCollection> FakeHttp(FakeHandler upstream, FakeHandler oauth) => services =>
    {
        services.AddHttpClient(ClaudeUpstream.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);
        services.AddHttpClient(ClaudeOAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => oauth);
    };

    public static string NewDataDirectory() =>
        Path.Combine(Path.GetTempPath(), "simple-router-tests", Guid.NewGuid().ToString("N"));

    public static async Task<RouterHost> StartAsync(params string[] models)
    {
        var dataDirectory = NewDataDirectory();
        var upstream = new FakeHandler();
        var oauth = new FakeHandler();
        var app = RouterApp.Build(Args(dataDirectory, models), FakeHttp(upstream, oauth));
        await RouterApp.InitializeAsync(app);
        await app.StartAsync();
        return new RouterHost(app, dataDirectory, upstream, oauth);
    }

    public Task<string> GatewayKeyAsync() => Services.GetRequiredService<GatewayKeyStore>().GetAsync();

    public async Task<HttpClient> AuthorizedClientAsync()
    {
        Client.DefaultRequestHeaders.Add(GatewayAuthMiddleware.RouterKeyHeader, await GatewayKeyAsync());
        return Client;
    }

    public Task LoginAsync(string accessToken = "access-current", DateTimeOffset? expiresAt = null, string refreshToken = "refresh-current") =>
        Services.GetRequiredService<ClaudeCredentialStore>()
            .SaveAsync(new ClaudeTokens(refreshToken, accessToken, expiresAt ?? DateTimeOffset.UtcNow.AddHours(1)));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { }
    }
}
