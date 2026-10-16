using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using SimpleRouter.Api;
using SimpleRouter.Claude;
using SimpleRouter.Codex;
using SimpleRouter.Dashboard;
using SimpleRouter.Dashboard.Components;
using SimpleRouter.Data;
using SimpleRouter.Gateway;
using SimpleRouter.Https;
using SimpleRouter.Local;
using SimpleRouter.Routing;
using SimpleRouter.SystemOne;
using SimpleRouter.Usage;

namespace SimpleRouter;

/// <summary>Composition root shared by the server, the CLI and tests.</summary>
public static class RouterApp
{
    private const long MaxRequestBodyBytes = 32 * 1024 * 1024;

    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            // Names the static web asset manifests; under a test host the entry assembly is not this one.
            ApplicationName = typeof(RouterApp).Assembly.GetName().Name,
        });
        // Serves the dashboard's assets from the build output in every environment, not only Development.
        builder.WebHost.UseStaticWebAssets();

        var section = builder.Configuration.GetSection(RouterOptions.Section);
        var options = section.Get<RouterOptions>() ?? new RouterOptions();
        var listenAddress = options.ResolveListenAddress();
        builder.Services.Configure<RouterOptions>(section);
        // The directory holds the key ring and encrypted tokens, so keep it owner-only.
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(options.DataDirectory);
        }
        else
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(options.DataDirectory, ownerOnly);
            File.SetUnixFileMode(options.DataDirectory, ownerOnly);
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            var certificate = kestrel.ApplicationServices.GetRequiredService<ServerCertificate>().Current;
            if (certificate is null)
            {
                kestrel.Listen(listenAddress, options.Port);
                return;
            }
            // With HTTPS available, the gateway key never crosses the LAN in plain text: HTTP stays on this machine.
            kestrel.Listen(IPAddress.IsLoopback(listenAddress) ? listenAddress : IPAddress.Loopback, options.Port);
            kestrel.Listen(listenAddress, options.HttpsPort, listen => listen.UseHttps(certificate));
        });

        var databasePath = Path.Combine(options.DataDirectory, "simple-router.db");
        builder.Services.AddDbContextFactory<RouterDbContext>(db => db.UseSqlite($"Data Source={databasePath}"));
        builder.Services.AddDataProtection()
            .SetApplicationName("SimpleRouter")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDirectory, "keys")));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SecretProtector>();
        builder.Services.AddSingleton(LanIdentity.System);
        builder.Services.AddSingleton<ServerCertificate>();
        builder.Services.AddSingleton<CertCreateCommand>();
        builder.Services.AddSingleton<GatewayKeyStore>();
        builder.Services.AddSingleton<ClaudeCredentialStore>();
        builder.Services.AddSingleton<ClaudeOAuthClient>();
        builder.Services.AddSingleton<ClaudeTokenProvider>();
        builder.Services.AddSingleton<ClaudeLoginCommand>();
        builder.Services.AddSingleton<ClaudeUpstream>();
        builder.Services.AddSingleton<IModelUpstream>(sp => sp.GetRequiredService<ClaudeUpstream>());
        builder.Services.AddSingleton<CodexCredentialStore>();
        builder.Services.AddSingleton<CodexOAuthClient>();
        builder.Services.AddSingleton<CodexTokenProvider>();
        builder.Services.AddSingleton<CodexLoginCommand>();
        builder.Services.AddSingleton<IModelUpstream, CodexUpstream>();
        builder.Services.AddSingleton<IModelUpstream, LocalUpstream>();
        builder.Services.AddSingleton<LocalHealth>();
        builder.Services.AddSingleton<SystemOneProxy>();
        builder.Services.AddHostedService<LocalHealthMonitor>();
        builder.Services.AddSingleton<IModelListSource, ClaudeModelList>();
        builder.Services.AddSingleton<IModelListSource, CodexModelList>();
        builder.Services.AddSingleton<ModelDiscovery>();
        builder.Services.AddHostedService<ModelDiscoveryMonitor>();
        builder.Services.AddSingleton<CatalogSeeder>();
        builder.Services.AddSingleton<ModelCooldowns>();
        builder.Services.AddSingleton<ModelSelector>();
        builder.Services.AddSingleton<MessageRouter>();
        builder.Services.AddSingleton<UsageRecorder>();
        builder.Services.AddSingleton<SubscriptionUsage>();
        builder.Services.AddSingleton<DashboardEvents>();
        builder.Services.AddSingleton<DashboardData>();
        builder.Services.AddSingleton<CatalogSettings>();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddHttpClient(ClaudeOAuthClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        // Streams can run for many minutes; cancellation follows the inbound request instead.
        builder.Services.AddHttpClient(ClaudeUpstream.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddHttpClient(CodexOAuthClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddHttpClient(CodexUpstream.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddHttpClient(LocalUpstream.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddHttpClient(SystemOneProxy.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddHttpClient(LocalHealth.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5));

        // The dashboard trusts only a loopback remote address, so the framework's FORWARDEDHEADERS_ENABLED switch must
        // not let any client rewrite that address with X-Forwarded-For.
        builder.Services.PostConfigure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.None);

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            app.Logger.LogInformation("Listening on {Addresses}; the dashboard answers only this machine.", string.Join(", ", app.Urls));
            var https = app.Urls.Select(u => new Uri(u)).FirstOrDefault(u => u.Scheme == Uri.UriSchemeHttps);
            if (https is not null)
            {
                app.Services.GetRequiredService<ServerCertificate>().LogStatus(app.Logger, listenAddress, https.Port);
            }
        });
        app.UseMiddleware<DashboardHostFilter>();
        app.UseMiddleware<GatewayAuthMiddleware>();
        app.MapModelsEndpoints();
        app.MapMessagesEndpoints();
        app.MapDarkFactoryEndpoints();
        app.MapSystemOneEndpoints();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }

    /// <summary>Applies migrations, seeds the routing catalog and generates the gateway key on first run.</summary>
    public static async Task InitializeAsync(WebApplication app, CancellationToken ct = default)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.MigrateAsync(ct);
        }
        await app.Services.GetRequiredService<CatalogSeeder>().SeedAsync(ct);

        var created = await app.Services.GetRequiredService<GatewayKeyStore>().EnsureAsync(ct);
        if (created)
        {
            app.Logger.LogInformation("Generated a new gateway key; run 'simple-router key show' to display it.");
        }
    }
}
