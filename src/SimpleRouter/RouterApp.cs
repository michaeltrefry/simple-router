using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SimpleRouter.Api;
using SimpleRouter.Claude;
using SimpleRouter.Data;
using SimpleRouter.Gateway;

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
        builder.Services.AddHttpClient(ClaudeOAuthClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        // Streams can run for many minutes; cancellation follows the inbound request instead.
        builder.Services.AddHttpClient(ClaudeUpstream.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseMiddleware<GatewayAuthMiddleware>();
        app.MapModelsEndpoints();
        app.MapMessagesEndpoints();
        return app;
    }

    /// <summary>Applies migrations and generates the gateway key on first run.</summary>
    public static async Task InitializeAsync(WebApplication app, CancellationToken ct = default)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.MigrateAsync(ct);
        }

        var created = await app.Services.GetRequiredService<GatewayKeyStore>().EnsureAsync(ct);
        if (created)
        {
            app.Logger.LogInformation("Generated a new gateway key; run 'simple-router key show' to display it.");
        }
    }
}
