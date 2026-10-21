using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Dashboard;
using SimpleRouter.Data;
using SimpleRouter.OpenRouter;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.SettingsHarness;

namespace SimpleRouter.Tests;

public class OpenRouterSettingsTests
{
    private static Task<RouterDbContext> DbAsync(RouterHost host) =>
        host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();

    [Fact]
    public async Task Provider_key_is_encrypted_never_redisplayed_and_can_be_replaced_and_removed()
    {
        var (host, _) = await StartAsync();
        await using var lifetime = host;
        using var context = Context(host);
        var cut = Render(context);
        await cut.ChangeAsync("#openrouter-connect .api-key", "openrouter-test-secret");
        await cut.ClickAsync("#openrouter-connect .save");
        Assert.DoesNotContain("openrouter-test-secret", cut.Markup);
        Assert.Contains("API key set", cut.Find("#openrouter-connect .api-key-state").TextContent);
        var store = host.Services.GetRequiredService<OpenRouterCredentialStore>();
        Assert.Equal("openrouter-test-secret", await store.GetAsync());
        await using (var db = await DbAsync(host))
        {
            var row = await db.ProviderCredentials.SingleAsync();
            Assert.Equal(ProviderKind.OpenRouter, row.Provider);
            Assert.DoesNotContain("openrouter-test-secret", row.ProtectedApiKey);
        }
        await cut.ChangeAsync("#openrouter-connect .api-key", "replacement-test-secret");
        await cut.ClickAsync("#openrouter-connect .save");
        Assert.Equal("replacement-test-secret", await store.GetAsync());
        Assert.DoesNotContain("replacement-test-secret", cut.Markup);
        await cut.ClickAsync("#openrouter-connect .remove");
        Assert.False(await store.HasKeyAsync());
        Assert.Null(await store.GetAsync());
    }

    [Theory]
    [InlineData("openrouter/auto", "low")]
    [InlineData("openrouter/auto", "medium")]
    [InlineData("openrouter/auto", "high")]
    [InlineData("openrouter/auto-beta", "xhigh")]
    [InlineData("openrouter/auto-beta", "max")]
    public async Task Auto_forms_save_cost_constraints_independently_of_reasoning(string upstream, string costTier)
    {
        var (host, _) = await StartAsync();
        await using var lifetime = host;
        using var context = Context(host);
        var cut = Render(context);
        await cut.ClickAsync("#add-openrouter-model");
        await cut.ChangeAsync("#openrouter-model-form .id", "or-auto");
        await cut.ChangeAsync("#openrouter-model-form .upstream", upstream);
        await cut.ChangeAsync("#openrouter-model-form .cost-tier", costTier);
        await cut.ChangeAsync("#openrouter-model-form .allowed-models", "vendor/*, vendor/model");
        await cut.ChangeAsync("#openrouter-model-form .excluded-models", "vendor/old");
        await cut.ChangeAsync("#openrouter-model-form .max-prompt-price", "0.5");
        await cut.ChangeAsync("#openrouter-model-form .max-completion-price", "2");
        await cut.ChangeAsync("#openrouter-model-form .tier-mid", true);
        await cut.ChangeAsync("#openrouter-model-form .effort-levels", "low, high");
        await cut.ChangeAsync("#openrouter-model-form .effort-default", "high");
        await cut.ClickAsync("#openrouter-model-form .save");
        Assert.Empty(cut.FindAll("#openrouter-model-form"));
        await using var db = await DbAsync(host);
        var model = await db.Models.SingleAsync(m => m.Id == "or-auto");
        Assert.Equal(ProviderKind.OpenRouter, model.Provider);
        Assert.Equal(upstream, model.UpstreamModelId);
        Assert.Equal(costTier, model.OpenRouter!.CostTier);
        Assert.Equal(["vendor/*", "vendor/model"], model.OpenRouter.AllowedModels);
        Assert.Equal(["vendor/old"], model.OpenRouter.ExcludedModels);
        Assert.Equal(0.5m, model.OpenRouter.MaxPromptPrice);
        Assert.Equal(2m, model.OpenRouter.MaxCompletionPrice);
        Assert.Equal(EffortLevel.High, model.Effort!.Default);
        Assert.Equal(EffortMode.ReasoningEffort, model.Effort.Mode);
        await cut.Row("model-settings", "or-auto").QuerySelector(".edit")!.ClickAsync(new());
        Assert.Equal("low, high", cut.Find("#openrouter-model-form .effort-levels").GetAttribute("value"));
        Assert.Equal("high", cut.Find("#openrouter-model-form .effort-default").GetAttribute("value"));
        Assert.Empty(cut.FindAll("#openrouter-model-form .effort-mode, #openrouter-model-form .effort-kwargs"));
        Assert.Equal(ModelTier.Mid, (await db.TierMembers.SingleAsync(m => m.ModelId == model.Id)).Tier);
        model.OpenRouter.AllowedModels!.Add("another/model");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Contains("another/model", (await db.Models.FindAsync("or-auto"))!.OpenRouter!.AllowedModels!);
    }

    [Fact]
    public async Task Fixed_model_form_keeps_price_ceilings_and_hides_auto_filters()
    {
        var (host, _) = await StartAsync();
        await using var lifetime = host;
        using var context = Context(host);
        var cut = Render(context);
        await cut.ClickAsync("#add-openrouter-model");
        await cut.ChangeAsync("#openrouter-model-form .id", "or-fixed");
        await cut.ChangeAsync("#openrouter-model-form .upstream", "vendor/model");
        Assert.Empty(cut.FindAll("#openrouter-model-form .cost-tier"));
        await cut.ChangeAsync("#openrouter-model-form .max-prompt-price", "3");
        await cut.ClickAsync("#openrouter-model-form .save");
        await using var db = await DbAsync(host);
        Assert.Equal(3m, (await db.Models.FindAsync("or-fixed"))!.OpenRouter!.MaxPromptPrice);
        Assert.Null((await db.Models.FindAsync("or-fixed"))!.Effort);
        await cut.Row("model-settings", "or-fixed").QuerySelector(".edit")!.ClickAsync(new());
        await cut.ChangeAsync("#openrouter-model-form .id", "or-renamed");
        await cut.ClickAsync("#openrouter-model-form .save");
        await cut.Row("model-settings", "or-renamed").QuerySelector(".remove")!.ClickAsync(new());
        await cut.Row("model-settings", "or-renamed").QuerySelector(".confirm-remove")!.ClickAsync(new());
        db.ChangeTracker.Clear();
        Assert.Null(await db.Models.FindAsync("or-fixed"));
        Assert.Null(await db.Models.FindAsync("or-renamed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_upstream_identity_discards_old_prices_even_when_renaming(bool rename)
    {
        var (host, _) = await StartAsync();
        await using var lifetime = host;
        var catalog = host.Services.GetRequiredService<CatalogSettings>();
        var input = new OpenRouterModelInput("priced-model", "vendor/old", true, null, true, false, null, null, [ModelTier.High]);
        await catalog.SaveOpenRouterModelAsync(input, null);
        await using (var db = await DbAsync(host))
        {
            db.ModelPrices.Add(new ModelPrice { ModelId = input.Id, InputPerMTok = 10, OutputPerMTok = 20 });
            await db.SaveChangesAsync();
        }
        var changed = input with { Id = rename ? "renamed-model" : input.Id, UpstreamModelId = "vendor/new" };
        await catalog.SaveOpenRouterModelAsync(changed, input.Id);
        await using var check = await DbAsync(host);
        Assert.Equal("vendor/new", (await check.Models.FindAsync(changed.Id))!.UpstreamModelId);
        Assert.Null(await check.ModelPrices.FindAsync(changed.Id));
        if (rename) Assert.Null(await check.ModelPrices.FindAsync(input.Id));
    }

    [Fact]
    public async Task Invalid_cost_tiers_negative_prices_and_fixed_auto_filters_are_rejected_without_persisting()
    {
        var (host, _) = await StartAsync();
        await using var lifetime = host;
        var catalog = host.Services.GetRequiredService<CatalogSettings>();
        var input = new OpenRouterModelInput("invalid-or", "openrouter/auto", true, null, true, false, null, new() { CostTier = "free" }, [ModelTier.Low]);
        await Assert.ThrowsAsync<SettingsException>(() => catalog.SaveOpenRouterModelAsync(input, null));
        await Assert.ThrowsAsync<SettingsException>(() => catalog.SaveOpenRouterModelAsync(input with { OpenRouter = new() { MaxPromptPrice = -1 } }, null));
        await Assert.ThrowsAsync<SettingsException>(() => catalog.SaveOpenRouterModelAsync(input with { UpstreamModelId = "vendor/model", OpenRouter = new() { CostTier = "low" } }, null));
        await using var db = await DbAsync(host);
        Assert.Null(await db.Models.FindAsync("invalid-or"));
    }

    [Fact]
    public async Task Upstream_latest_aliases_keep_their_tilde_but_router_ids_cannot_start_with_one()
    {
        var (host, _) = await StartAsync();
        await using var lifetime = host;
        var catalog = host.Services.GetRequiredService<CatalogSettings>();
        var input = new OpenRouterModelInput("deepseek-pro", "~deepseek/deepseek-pro-latest", true, null, true, false, null, null, [ModelTier.Mid]);
        await catalog.SaveOpenRouterModelAsync(input, null);
        await Assert.ThrowsAsync<SettingsException>(() => catalog.SaveOpenRouterModelAsync(input with { Id = "~deepseek-pro" }, null));
        await Assert.ThrowsAsync<SettingsException>(() => catalog.SaveOpenRouterModelAsync(input with { Id = "other", UpstreamModelId = "~~deepseek/x" }, null));
        await using var db = await DbAsync(host);
        Assert.Equal("~deepseek/deepseek-pro-latest", (await db.Models.FindAsync("deepseek-pro"))!.UpstreamModelId);
        Assert.Null(await db.Models.FindAsync("other"));
    }
}
