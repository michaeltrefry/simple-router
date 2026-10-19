using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleRouter.Dashboard;
using SimpleRouter.Data;
using SimpleRouter.Local;
using SimpleRouter.OpenRouter;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public sealed class OpenRouterDiscoveryTests
{
    private const string Catalog = """
        {"data":[
          {"id":"vendor/fixed","context_length":32000,"supported_parameters":["tools"],
           "architecture":{"input_modalities":["text","image"]},
           "reasoning":{"supported_efforts":["low","medium","high"],"default_effort":"medium"},
           "pricing":{"prompt":"0.000002","completion":"0.000006","input_cache_read":"0.0000005","input_cache_write":"0.0000025","input_cache_write_1h":"0.000004"}},
          {"id":"vendor/text","context_length":4096,"supported_parameters":[],
           "architecture":{"input_modalities":["text"]},"pricing":{"prompt":"0","completion":"0"}},
          {"id":"openrouter/auto","context_length":0,"pricing":{"prompt":"-1","completion":"-1"}},
          {"id":"openrouter/auto-beta","pricing":{"prompt":"-1"}},
          {"id":"nvidia/switchyard","pricing":{"prompt":"-1","completion":"-1"}},
          {"id":"vendor/unconfigured","pricing":{"prompt":"0.000001","completion":"0.000004"}}
        ]}
        """;

    [Fact]
    public async Task Refresh_updates_only_configured_fixed_metadata_and_converts_per_token_prices()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Store.SetAsync("test-shared-key");
        var list = await f.Discovery.RefreshAsync(ProviderKind.OpenRouter);

        Assert.Null(list.LastError);
        var request = Assert.Single(f.Http.Requests);
        Assert.Equal("https://openrouter.test/api/v1/models", request.Uri.ToString());
        Assert.Equal("Bearer test-shared-key", request.Headers["authorization"]);
        await using var db = f.Db.CreateDbContext();
        var fixedModel = await db.Models.FindAsync("fixed");
        Assert.Equal(32000, fixedModel!.ContextWindow);
        Assert.True(fixedModel.SupportsTools);
        Assert.True(fixedModel.SupportsVision);
        Assert.Equal([EffortLevel.Low, EffortLevel.Medium, EffortLevel.High], fixedModel.Effort!.Levels);
        Assert.Equal(EffortLevel.Medium, fixedModel.Effort.Default);
        Assert.Equal(12345, fixedModel.MaxPromptTokens);
        var text = await db.Models.FindAsync("text");
        Assert.False(text!.SupportsTools);
        Assert.False(text.SupportsVision);
        var price = await db.ModelPrices.FindAsync("fixed");
        Assert.Equal(2m, price!.InputPerMTok);
        Assert.Equal(6m, price.OutputPerMTok);
        Assert.Equal(.5m, price.CacheReadPerMTok);
        Assert.Equal(2.5m, price.CacheWritePerMTok);
        Assert.Equal(4m, price.CacheWrite1hPerMTok);
        Assert.Equal(0m, (await db.ModelPrices.FindAsync("text"))!.InputPerMTok);
        Assert.Null(await db.ModelPrices.FindAsync("vendor/unconfigured"));
        Assert.Null(await db.Models.FindAsync("vendor/unconfigured"));
        Assert.Null(await db.ModelPrices.FindAsync("auto"));
    }

    [Fact]
    public async Task Refresh_preserves_a_saved_effort_default_and_level_restriction()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Store.SetAsync("test-shared-key");
        await using (var db = f.Db.CreateDbContext())
        {
            (await db.Models.FindAsync("fixed"))!.Effort = new EffortMapping(EffortMode.ReasoningEffort,
                [EffortLevel.Low], Default: EffortLevel.Low);
            await db.SaveChangesAsync();
        }
        await f.Discovery.RefreshAsync(ProviderKind.OpenRouter);
        await using var check = f.Db.CreateDbContext();
        var effort = (await check.Models.FindAsync("fixed"))!.Effort!;
        Assert.Equal(EffortLevel.Low, effort.Default);
        Assert.Equal([EffortLevel.Low], effort.Levels);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unknown_core_prices_neither_keep_stale_estimates_nor_create_cache_only_prices(bool previouslyPriced)
    {
        await using var f = await Fixture.CreateAsync();
        await f.Store.SetAsync("test-shared-key");
        if (previouslyPriced) await f.Discovery.RefreshAsync(ProviderKind.OpenRouter);
        f.Http.Respond = _ => Json("""
            {"data":[
              {"id":"vendor/fixed","pricing":{"prompt":"-1","completion":"-1","input_cache_read":"0.000001"}},
              {"id":"vendor/text","pricing":{"prompt":"0","input_cache_write":"0.000002"}}
            ]}
            """);

        var refreshed = await f.Discovery.RefreshAsync(ProviderKind.OpenRouter);

        Assert.Null(refreshed.LastError);
        await using var db = f.Db.CreateDbContext();
        Assert.Null(await db.ModelPrices.FindAsync("fixed"));
        Assert.Null(await db.ModelPrices.FindAsync("text"));
    }

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"error\":\"secret-body\"}")]
    [InlineData("secret-body-not-json")]
    [InlineData("{\"data\":[{\"id\":\"vendor/fixed\",\"pricing\":{\"prompt\":\"secret-body\"}}]}")]
    public async Task Failed_refresh_retains_last_good_list_and_metadata_without_response_fragments(string body)
    {
        await using var f = await Fixture.CreateAsync();
        await f.Store.SetAsync("test-shared-key");
        var good = await f.Discovery.RefreshAsync(ProviderKind.OpenRouter);
        f.Http.Respond = _ => Json(body);
        var failed = await f.Discovery.RefreshAsync(ProviderKind.OpenRouter);
        Assert.NotNull(failed.LastError);
        Assert.DoesNotContain("secret-body", failed.LastError);
        Assert.Equal(good.ModelIds, failed.ModelIds);
        Assert.Equal(good.FetchedAt, failed.FetchedAt);
        await using var db = f.Db.CreateDbContext();
        Assert.Equal(32000, (await db.Models.FindAsync("fixed"))!.ContextWindow);
        Assert.Equal(6m, (await db.ModelPrices.FindAsync("fixed"))!.OutputPerMTok);
    }

    [Fact]
    public async Task Transport_exception_is_sanitized_and_missing_key_does_not_fetch()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Discovery.RefreshAllAsync();
        Assert.Empty(f.Http.Requests);
        await f.Store.SetAsync("test-shared-key");
        f.Http.Respond = _ => throw new HttpRequestException("test-shared-key secret-body");
        var failed = await f.Discovery.RefreshAsync(ProviderKind.OpenRouter);
        Assert.Equal("OpenRouter model list could not be read.", failed.LastError);
        Assert.Null(failed.ModelIds);
    }

    [Fact]
    public async Task Shared_key_gates_fixed_and_auto_models_and_successful_list_excludes_missing_fixed_only()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.All(await f.Selector.StatusesAsync(default), s => Assert.Equal(ModelStatus.NotLoggedIn, s.Status));
        await f.Store.SetAsync("test-shared-key");
        Assert.All(await f.Selector.StatusesAsync(default), s => Assert.Equal(ModelStatus.Eligible, s.Status));
        f.Http.Respond = _ => Json("""{"data":[{"id":"vendor/fixed"}]}""");
        await f.Discovery.RefreshAsync(ProviderKind.OpenRouter);
        var statuses = (await f.Selector.StatusesAsync(default)).ToDictionary(s => s.ModelId, s => s.Status);
        Assert.Equal(ModelStatus.UnknownModel, statuses["missing"]);
        Assert.Equal(ModelStatus.Eligible, statuses["fixed"]);
        Assert.Equal(ModelStatus.Eligible, statuses["auto"]);
        Assert.Equal(ModelStatus.Eligible, statuses["auto-beta"]);
        await f.Store.SetAsync(null);
        Assert.All(await f.Selector.StatusesAsync(default), s => Assert.Equal(ModelStatus.NotLoggedIn, s.Status));
    }

    [Fact]
    public async Task Selection_uses_tiers_and_discovered_capabilities_but_not_local_health_or_conversation_caps()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Store.SetAsync("test-shared-key");
        await f.Discovery.RefreshAsync(ProviderKind.OpenRouter);
        var first = await f.Selector.SelectAsync(ModelTier.High, new("one", null), new(10, 10, true), default);
        var second = await f.Selector.SelectAsync(ModelTier.High, new("two", null), new(10, 10, true), default);
        Assert.Equal("fixed", first!.Model.Id);
        Assert.Equal("fixed", second!.Model.Id);
        first.Hold.Dispose();
        second.Hold.Dispose();
        Assert.Null(await f.Selector.SelectAsync(ModelTier.Low, new("empty", null), new(10, 10, false), default));
        Assert.Null(await f.Selector.SelectAsync(ModelTier.High, new("large", null), new(40000, 10, false), default));
    }

    [Theory]
    [InlineData("openrouter/auto", true)]
    [InlineData("openrouter/auto-beta", true)]
    [InlineData("vendor/fixed", true)]
    [InlineData("vendor/fixed-20260101", false)]
    [InlineData("vendor/missing", false)]
    public void OpenRouter_membership_is_exact_except_auto_variants(string id, bool listed) =>
        Assert.Equal(listed, ModelDiscovery.Lists(ProviderKind.OpenRouter, ["vendor/fixed"], id));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public DbFactory Db { get; private set; } = null!;
        public FakeHandler Http { get; } = new() { Respond = _ => Json(Catalog) };
        public OpenRouterCredentialStore Store { get; private set; } = null!;
        public ModelDiscovery Discovery { get; private set; } = null!;
        public ModelSelector Selector { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            await f._connection.OpenAsync();
            f.Db = new DbFactory(new DbContextOptionsBuilder<RouterDbContext>().UseSqlite(f._connection).Options);
            var secrets = new SecretProtector(new EphemeralDataProtectionProvider());
            f.Store = new(f.Db, secrets);
            var options = Options.Create(new RouterOptions());
            options.Value.OpenRouter.ApiBaseUrl = "https://openrouter.test/api/v1";
            var clients = new Clients(f.Http);
            f.Discovery = new([new OpenRouterModelList(clients, f.Store, options)], f.Db, TimeProvider.System, NullLogger<ModelDiscovery>.Instance);
            f.Selector = new(f.Db, [new Wired()], new LocalHealth(clients, f.Db, secrets, NullLogger<LocalHealth>.Instance),
                new LocalConversations(options, TimeProvider.System), new DashboardEvents(), TimeProvider.System, NullLogger<ModelSelector>.Instance);
            await using var db = f.Db.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            foreach (var (id, upstream) in new[] { ("fixed", "vendor/fixed"), ("text", "vendor/text"), ("missing", "vendor/missing"), ("auto", "openrouter/auto"), ("auto-beta", "openrouter/auto-beta") })
                db.Models.Add(new CatalogModel { Id = id, Provider = ProviderKind.OpenRouter, UpstreamModelId = upstream, Enabled = true, MaxConversations = 1, MaxPromptTokens = id == "fixed" ? 12345 : null });
            db.TierMembers.AddRange(new TierMember { ModelId = "text", Tier = ModelTier.High, Position = 0 }, new TierMember { ModelId = "fixed", Tier = ModelTier.High, Position = 1 });
            await db.SaveChangesAsync();
            return f;
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class DbFactory(DbContextOptions<RouterDbContext> options) : IDbContextFactory<RouterDbContext>
    {
        public RouterDbContext CreateDbContext() => new(options);
    }

    private sealed class Clients(FakeHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Wired : IModelUpstream
    {
        public ProviderKind Provider => ProviderKind.OpenRouter;
        public Task<UpstreamResult> SendAsync(UpstreamCall call) => throw new NotSupportedException();
    }
}
