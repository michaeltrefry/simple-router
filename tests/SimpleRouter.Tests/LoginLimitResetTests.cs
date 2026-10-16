using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Claude;
using SimpleRouter.Cli;
using SimpleRouter.Codex;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using SubscriptionUsage = SimpleRouter.Usage.SubscriptionUsage;

namespace SimpleRouter.Tests;

public class LoginLimitResetTests
{
    [Theory]
    [InlineData(ProviderKind.Claude, false)]
    [InlineData(ProviderKind.Claude, true)]
    [InlineData(ProviderKind.Codex, false)]
    [InlineData(ProviderKind.Codex, true)]
    public async Task Cli_login_clears_only_its_provider_and_running_router_observes_it(ProviderKind provider, bool discoveryFails)
    {
        await using var host = await RouterHost.StartAsync();
        await SeedAsync(host);
        var before = await host.Services.GetRequiredService<SubscriptionUsage>().ReportAsync(default);
        Assert.True(before.AllExhausted);

        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var ids = await db.Models.Where(m => m.Provider == provider).Select(m => m.UpstreamModelId).ToListAsync();
        var models = new FakeHandler
        {
            Respond = _ => discoveryFails ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json(provider == ProviderKind.Claude
                    ? new JsonObject { ["data"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()) }.ToJsonString()
                    : new JsonObject { ["models"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["slug"] = id }).ToArray()) }.ToJsonString()),
        };
        var output = new StringWriter();
        var input = new ClaudeLoginTests.CallbackReader(() =>
        {
            var url = output.ToString().Split('\n').First(l => l.StartsWith("http"));
            return "code#" + QueryHelpers.ParseQuery(new Uri(url.Trim()).Query)["state"];
        });
        var exit = await CommandLine.RunAsync(["login", provider.ToString().ToLowerInvariant(), .. RouterHost.Args(host.DataDirectory)],
            input, output, RouterHost.FakeHttp(models, ClaudeIssuer(), codex: models, codexAuth: CodexIssuer()));

        Assert.Equal(0, exit);
        Assert.Single(models.Requests);
        Assert.Equal("Bearer new-access", models.Requests[0].Headers["authorization"]);
        var remaining = await db.ModelCooldowns.AsNoTracking().ToListAsync();
        var ownIds = (await db.Models.Where(m => m.Provider == provider).Select(m => m.Id).ToListAsync()).ToHashSet();
        Assert.DoesNotContain(remaining, c => ownIds.Contains(c.ModelId));
        Assert.Equal(await db.Models.CountAsync(m => m.Provider != provider), remaining.Count);
        Assert.All(remaining, c => Assert.Equal(FaultKind.Limited, c.Reason));
        Assert.Equal(123, (await db.RequestUsages.SingleAsync()).InputTokens);

        var statuses = await host.Services.GetRequiredService<ModelSelector>().StatusesAsync(default);
        Assert.All(statuses.Where(s => s.Provider == provider && !s.ModelId.StartsWith("disabled-login-")),
            s => Assert.Equal(ModelStatus.Eligible, s.Status));
        var report = await host.Services.GetRequiredService<SubscriptionUsage>().ReportAsync(default);
        Assert.False(report.AllExhausted);
        Assert.Null(report.ResumesAt);
        Assert.Equal("ok", report.Credentials.Single(c => c.Provider == provider.ToString().ToLowerInvariant()).State);
        Assert.Equal("cooling", report.Credentials.Single(c => c.Provider != provider.ToString().ToLowerInvariant()).State);
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    public async Task Rejected_login_preserves_credentials_and_cooldowns(ProviderKind provider)
    {
        await using var host = await RouterHost.StartAsync();
        await SeedAsync(host);
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var count = await db.ModelCooldowns.CountAsync();
        if (provider == ProviderKind.Claude)
        {
            host.OAuth.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest);
            var login = host.Services.GetRequiredService<ClaudeOAuthClient>().StartLogin();
            await Assert.ThrowsAsync<ClaudeOAuthException>(() => host.Services.GetRequiredService<ClaudeLoginCommand>()
                .CompleteAsync(login, "code#" + login.State));
        }
        else
        {
            host.CodexAuth.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest);
            var device = new DeviceCode("dev", "code", TimeSpan.FromMilliseconds(1), "http://codex-auth.test/device");
            await Assert.ThrowsAsync<CodexOAuthException>(() => host.Services.GetRequiredService<CodexLoginCommand>().CompleteAsync(device));
        }
        Assert.Equal(count, await db.ModelCooldowns.CountAsync());
        Assert.Equal("refresh-current", (await host.Services.GetRequiredService<ClaudeCredentialStore>().GetAsync())!.RefreshToken);
        Assert.Equal("codex-refresh-current", (await host.Services.GetRequiredService<CodexCredentialStore>().GetAsync())!.RefreshToken);
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    public async Task Token_refresh_preserves_cooldowns(ProviderKind provider)
    {
        await using var host = await RouterHost.StartAsync();
        await SeedAsync(host);
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var count = await db.ModelCooldowns.CountAsync();
        if (provider == ProviderKind.Claude)
        {
            await host.LoginAsync(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
            host.OAuth.Respond = ClaudeIssuer().Respond;
            Assert.Equal("new-access", await host.Services.GetRequiredService<ClaudeTokenProvider>().GetAccessTokenAsync());
        }
        else
        {
            await host.LoginCodexAsync(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
            host.CodexAuth.Respond = CodexIssuer().Respond;
            Assert.Equal("new-access", (await host.Services.GetRequiredService<CodexTokenProvider>().GetAccessAsync()).AccessToken);
        }
        Assert.Equal(count, await db.ModelCooldowns.CountAsync());
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    public async Task Failed_credential_write_rolls_back_cooldown_reset(ProviderKind provider)
    {
        await using var host = await RouterHost.StartAsync();
        await SeedAsync(host);
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var count = await db.ModelCooldowns.CountAsync();
        var trigger = provider == ProviderKind.Claude
            ? "CREATE TRIGGER reject_login BEFORE UPDATE ON ClaudeCredentials BEGIN SELECT RAISE(ABORT, 'test failure'); END"
            : "CREATE TRIGGER reject_login BEFORE UPDATE ON CodexCredentials BEGIN SELECT RAISE(ABORT, 'test failure'); END";
        await db.Database.ExecuteSqlRawAsync(trigger);

        if (provider == ProviderKind.Claude)
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => host.Services.GetRequiredService<ClaudeCredentialStore>()
                .SaveLoginAsync(new ClaudeTokens("new-refresh", "new-access", DateTimeOffset.UtcNow.AddHours(1))));
        }
        else
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => host.Services.GetRequiredService<CodexCredentialStore>()
                .SaveLoginAsync(new CodexTokens("new-refresh", "new-access", DateTimeOffset.UtcNow.AddHours(1), "new-account")));
        }
        Assert.Equal(count, await db.ModelCooldowns.CountAsync());
        Assert.Equal("refresh-current", (await host.Services.GetRequiredService<ClaudeCredentialStore>().GetAsync())!.RefreshToken);
        Assert.Equal("codex-refresh-current", (await host.Services.GetRequiredService<CodexCredentialStore>().GetAsync())!.RefreshToken);
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    public async Task Cancelled_login_preserves_cooldowns(ProviderKind provider)
    {
        await using var host = await RouterHost.StartAsync();
        await SeedAsync(host);
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var count = await db.ModelCooldowns.CountAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (provider == ProviderKind.Claude)
        {
            var login = host.Services.GetRequiredService<ClaudeOAuthClient>().StartLogin();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Services.GetRequiredService<ClaudeLoginCommand>()
                .CompleteAsync(login, "code#" + login.State, cancellation.Token));
        }
        else
        {
            var device = new DeviceCode("dev", "code", TimeSpan.FromMilliseconds(1), "http://codex-auth.test/device");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Services.GetRequiredService<CodexLoginCommand>()
                .CompleteAsync(device, cancellation.Token));
        }
        Assert.Equal(count, await db.ModelCooldowns.CountAsync());
    }

    private static async Task SeedAsync(RouterHost host)
    {
        await host.LoginAsync();
        await host.LoginCodexAsync();
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        // Disabled and untiered models must not regain the old account's limits when enabled later.
        foreach (var provider in new[] { ProviderKind.Claude, ProviderKind.Codex })
        {
            var id = $"disabled-login-{provider}";
            db.Models.Add(new CatalogModel { Id = id, UpstreamModelId = id, Provider = provider });
        }
        await db.SaveChangesAsync();
        foreach (var model in await db.Models.ToListAsync())
        {
            db.ModelCooldowns.Add(new ModelCooldown { ModelId = model.Id, Until = DateTimeOffset.UtcNow.AddDays(3), Reason = FaultKind.Limited });
        }
        db.RequestUsages.Add(new RequestUsage { ModelId = "history", Provider = ProviderKind.Claude, InputTokens = 123 });
        await db.SaveChangesAsync();
    }

    private static FakeHandler ClaudeIssuer() => new()
    {
        Respond = _ => Json("""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}"""),
    };

    private static FakeHandler CodexIssuer() => new()
    {
        Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/api/accounts/deviceauth/usercode" => Json("""{"device_auth_id":"dev","user_code":"code","interval":"1"}"""),
            "/api/accounts/deviceauth/token" => Json("""{"authorization_code":"code","code_challenge":"c","code_verifier":"v"}"""),
            "/oauth/token" => Json($$"""{"id_token":"{{CodexLoginTests.IdToken("new-account")}}","access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        },
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}
