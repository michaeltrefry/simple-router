using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Dashboard;
using SimpleRouter.Data;
using SimpleRouter.RouterCom;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using SimpleRouter.Usage;
using static SimpleRouter.Tests.Support.ResponsesFixtures;
using static SimpleRouter.Tests.Support.SettingsHarness;

namespace SimpleRouter.Tests;

public class RouterComRoutingTests
{
    [Fact]
    public async Task Rate_limit_fails_over_within_the_tier_and_records_actual_cost_and_model()
    {
        var completed = JsonNode.Parse(Completed(input: 120, output: 45, cached: 100,
            outputItems: """[{"type":"message","content":[{"type":"output_text","text":"answer"}]}]"""))!.AsObject();
        completed["response"]!["model"] = "resolved-model";
        completed["response"]!["usage"]!["cost"] = .00234m;
        var handler = new FakeHandler
        {
            Respond = request => JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!["model"]!.GetValue<string>() == "limited"
                ? Json("private body", HttpStatusCode.TooManyRequests)
                : Json(completed["response"]!.ToJsonString()),
        };
        await using var host = await RouterHost.StartAsync(s => s.AddHttpClient(RouterComUpstream.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));
        await host.Services.GetRequiredService<RouterComCredentialStore>().SetAsync("test-router-key");
        var catalog = host.Services.GetRequiredService<CatalogSettings>();
        foreach (var id in new[] { "limited", "fallback" })
            await catalog.SaveRouterComModelAsync(new("router-" + id, id, true, null, true, false, [ModelTier.Max]), null);
        var client = await host.AuthorizedClientAsync();
        using var request = Message("flex-failover", "max");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("answer", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.True(JsonNode.Parse(r.Body)!["allow_flex_tier"]!.GetValue<bool>()));
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        RequestUsage? row = null;
        for (var i = 0; i < 100 && row is null; i++)
        {
            await using var db = await factory.CreateDbContextAsync();
            row = await db.RequestUsages.SingleOrDefaultAsync();
            if (row is null) await Task.Delay(25);
        }
        Assert.NotNull(row);
        Assert.Equal("router-fallback", row.ModelId);
        Assert.Equal(ProviderKind.RouterCom, row.Provider);
        Assert.Equal("resolved-model", row.ResolvedModelId);
        Assert.Equal("reported", row.CostSource);
        Assert.Equal(2340, row.CostUsdMicros);
        Assert.Equal((20L, 45L, 100L), (row.InputTokens, row.OutputTokens, row.CacheReadTokens));
        await using var check = await factory.CreateDbContextAsync();
        Assert.Equal("router-fallback", (await check.SessionPins.SingleAsync()).ModelId);
        Assert.Equal(FaultKind.Limited, (await check.ModelCooldowns.SingleAsync()).Reason);
        var account = Assert.Single(await host.Services.GetRequiredService<DashboardData>().AccountsAsync(default), a => a.Provider == ProviderKind.RouterCom);
        Assert.True(account.LoggedIn);
        Assert.Equal("ok", account.State);
    }

    [Fact]
    public async Task Flex_without_reported_cost_does_not_assume_standard_pricing()
    {
        await using var host = await RouterHost.StartAsync();
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ModelPrices.Add(new ModelPrice { ModelId = "flex-entry", InputPerMTok = 50, OutputPerMTok = 100 });
            await db.SaveChangesAsync();
        }
        var model = new CatalogModel { Id = "flex-entry", Provider = ProviderKind.RouterCom, UpstreamModelId = "test-model" };
        await host.Services.GetRequiredService<UsageRecorder>().RecordAsync(new(null, model, ModelTier.Low, 200,
            new TokenUsage(10, 20, 0, 0), TimeSpan.Zero, null));
        await using var check = await factory.CreateDbContextAsync();
        Assert.Equal("unknown", (await check.RequestUsages.SingleAsync()).CostSource);
    }

    [Fact]
    public async Task Seed_and_override_keep_flex_controls_separate_from_provider_credentials()
    {
        var directory = RouterHost.NewDataDirectory();
        Directory.CreateDirectory(directory);
        var seedPath = Path.Combine(directory, "test-seed.json");
        await File.WriteAllTextAsync(seedPath, """
          {"defaultTier":"low","models":[{"id":"seed-flex","provider":"routercom","upstreamModelId":"test-model","enabled":true,
            "routerCom":{"allowFlexTier":true,"fallbackTimeoutSeconds":30,"providerTimeoutSeconds":140}}],
           "tiers":{"low":["seed-flex"]},"aliases":[]}
          """);
        await File.WriteAllTextAsync(Path.Combine(directory, "seed.local.json"), """
          {"models":[{"id":"seed-flex","routerCom":{"allowFlexTier":false,"fallbackTimeoutSeconds":20,"providerTimeoutSeconds":120}}]}
          """);
        await using var host = await RouterHost.StartInAsync(directory, $"--SimpleRouter:SeedFile={seedPath}");
        var model = Assert.Single((await host.Services.GetRequiredService<CatalogSettings>().GetAsync()).Models);
        Assert.False(model.RouterCom!.AllowFlexTier);
        Assert.Equal(20, model.RouterCom.FallbackTimeoutSeconds);
        Assert.Equal(ModelStatus.NotLoggedIn, (await host.Services.GetRequiredService<ModelSelector>().StatusesAsync(default)).Single().Status);
        Assert.False(await host.Services.GetRequiredService<RouterComCredentialStore>().HasKeyAsync());
    }
}
