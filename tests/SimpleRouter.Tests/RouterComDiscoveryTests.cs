using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Dashboard;
using SimpleRouter.Data;
using SimpleRouter.OpenRouter;
using SimpleRouter.RouterCom;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.SettingsHarness;

namespace SimpleRouter.Tests;

public class RouterComDiscoveryTests
{
    private const string Catalog = """
        {"data":[
          {"id":"test-model","router":{"surfaces":["responses","messages"],"limits":{"context_window":32000},
            "capabilities":{"tools":{"supported":true},"modalities":{"input":["text","image"]},"reasoning":{"efforts":[{"value":"low"},{"value":"high"}],"default_effort":"high"}},
            "pricing":{"input":"2","output":"6","cache_read_input":"0.5","cache_write_input_5m":"2.5","cache_write_input_1h":"4"}}},
          {"id":"text-model","router":{"surfaces":["responses"],"capabilities":{"tools":{"supported":false},"modalities":{"input":["text"]}},"pricing":{"input":"0","output":"0"}}},
          {"id":"my-alias","router":{"surfaces":["responses"],"limits":{"context_window":10000},"capabilities":{}},"router_alias":{"candidates":[]}},
          {"id":"decision-only","router":{"surfaces":["systemone","decisions"]}}
        ]}
        """;

    [Fact]
    public async Task Discovery_gates_on_its_own_key_and_updates_only_its_models_without_multiplying_prices()
    {
        var handler = new FakeHandler { Respond = _ => Json(Catalog) };
        await using var host = await RouterHost.StartAsync(s => s.AddHttpClient(RouterComUpstream.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));
        var catalog = host.Services.GetRequiredService<CatalogSettings>();
        foreach (var id in new[] { "test-model", "text-model", "my-alias", "missing" })
            await catalog.SaveRouterComModelAsync(new("router-" + id, id, true, null, true, false, [ModelTier.High]), null);
        await catalog.SaveOpenRouterModelAsync(new("other-provider", "test-model", true, 42, false, false, null, null, []), null);
        var discovery = host.Services.GetRequiredService<ModelDiscovery>();
        var selector = host.Services.GetRequiredService<ModelSelector>();
        await host.Services.GetRequiredService<OpenRouterCredentialStore>().SetAsync("unrelated-key");
        await discovery.RefreshAllAsync();
        Assert.Empty(handler.Requests);
        Assert.All((await selector.StatusesAsync(default)).Where(s => s.Provider == ProviderKind.RouterCom), s => Assert.Equal(ModelStatus.NotLoggedIn, s.Status));
        await host.Services.GetRequiredService<RouterComCredentialStore>().SetAsync("routercom-catalog-key");
        var list = await discovery.RefreshAsync(ProviderKind.RouterCom);
        Assert.Null(list.LastError);
        Assert.DoesNotContain("decision-only", list.ModelIds!);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("http://routercom.test/v1/models", sent.Uri.ToString());
        Assert.Equal("Bearer routercom-catalog-key", sent.Headers["authorization"]);
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        var model = (await db.Models.FindAsync("router-test-model"))!;
        Assert.Equal(32000, model.ContextWindow);
        Assert.True(model.SupportsVision);
        Assert.True(model.SupportsTools);
        Assert.Equal([EffortLevel.Low, EffortLevel.High], model.Effort!.Levels);
        Assert.Equal(EffortLevel.High, model.Effort.Default);
        Assert.False((await db.Models.FindAsync("router-text-model"))!.SupportsTools);
        Assert.Equal(42, (await db.Models.FindAsync("other-provider"))!.ContextWindow);
        var price = (await db.ModelPrices.FindAsync("router-test-model"))!;
        Assert.Equal((2m, 6m, .5m, 2.5m, 4m), (price.InputPerMTok, price.OutputPerMTok, price.CacheReadPerMTok, price.CacheWritePerMTok, price.CacheWrite1hPerMTok));
        Assert.Null(await db.ModelPrices.FindAsync("router-my-alias"));
        Assert.Equal(ModelStatus.UnknownModel, (await selector.StatusesAsync(default)).Single(s => s.ModelId == "router-missing").Status);
        Assert.Equal(ModelStatus.Eligible, (await selector.StatusesAsync(default)).Single(s => s.ModelId == "router-test-model").Status);
    }

    [Theory]
    [InlineData("not-json-private-body")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"id\":\"test-model\",\"router\":{\"pricing\":{\"input\":\"private-body\"}}}]}")]
    public async Task Failed_refresh_keeps_good_list_and_never_stores_response_fragments(string body)
    {
        var handler = new FakeHandler { Respond = _ => Json(Catalog) };
        await using var host = await RouterHost.StartAsync(s => s.AddHttpClient(RouterComUpstream.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));
        await host.Services.GetRequiredService<RouterComCredentialStore>().SetAsync("test-key");
        var discovery = host.Services.GetRequiredService<ModelDiscovery>();
        var good = await discovery.RefreshAsync(ProviderKind.RouterCom);
        handler.Respond = _ => Json(body);
        var failed = await discovery.RefreshAsync(ProviderKind.RouterCom);
        Assert.NotNull(failed.LastError);
        Assert.DoesNotContain("private-body", failed.LastError);
        Assert.Equal(good.ModelIds, failed.ModelIds);
        Assert.Equal(good.FetchedAt, failed.FetchedAt);
    }
}
