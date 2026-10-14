using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SimpleRouter.Api;
using SimpleRouter.Claude;
using SimpleRouter.Codex;
using SimpleRouter.Data;
using SimpleRouter.Gateway;
using SimpleRouter.Local;
using SimpleRouter.Routing;

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
        });

        var section = builder.Configuration.GetSection(RouterOptions.Section);
        var options = section.Get<RouterOptions>() ?? new RouterOptions();
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
            kestrel.Listen(IPAddress.Loopback, options.Port);
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
        });

        var databasePath = Path.Combine(options.DataDirectory, "simple-router.db");
        builder.Services.AddDbContextFactory<RouterDbContext>(db => db.UseSqlite($"Data Source={databasePath}"));
        builder.Services.AddDataProtection()
            .SetApplicationName("SimpleRouter")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDirectory, "keys")));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SecretProtector>();
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
        builder.Services.AddHostedService<LocalHealthMonitor>();
        builder.Services.AddSingleton<CatalogSeeder>();
        builder.Services.AddSingleton<ModelSelector>();
        builder.Services.AddSingleton<MessageRouter>();
        builder.Services.AddHttpClient(ClaudeOAuthClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        // Streams can run for many minutes; cancellation follows the inbound request instead.
        builder.Services.AddHttpClient(ClaudeUpstream.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddHttpClient(CodexOAuthClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddHttpClient(CodexUpstream.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddHttpClient(LocalUpstream.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddHttpClient(LocalHealth.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5));

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseMiddleware<GatewayAuthMiddleware>();
        app.MapModelsEndpoints();
        app.MapMessagesEndpoints();
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
