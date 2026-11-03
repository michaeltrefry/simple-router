using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Dashboard;
using SimpleRouter.Data;
using SimpleRouter.OpenRouter;
using SimpleRouter.RouterCom;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.SettingsHarness;

namespace SimpleRouter.Tests;

public class RouterComSettingsTests
{
    [Fact]
    public async Task Keys_are_encrypted_independent_and_never_redisplayed()
    {
        var (host, _) = await StartAsync();
        await using var lifetime = host;
        var other = host.Services.GetRequiredService<OpenRouterCredentialStore>();
        await other.SetAsync("openrouter-independent-secret");
        using var context = Context(host);
        var cut = Render(context);
        await cut.ChangeAsync("#routercom-connect .api-key", "routercom-test-secret");
        await cut.ClickAsync("#routercom-connect .save");
        Assert.DoesNotContain("routercom-test-secret", cut.Markup);
        Assert.Contains("API key set", cut.Find("#routercom-connect .api-key-state").TextContent);
        var store = host.Services.GetRequiredService<RouterComCredentialStore>();
        Assert.Equal("routercom-test-secret", await store.GetAsync());
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        Assert.DoesNotContain("routercom-test-secret", (await db.ProviderCredentials.FindAsync(ProviderKind.RouterCom))!.ProtectedApiKey);
        await cut.ClickAsync("#routercom-connect .remove");
        Assert.Null(await store.GetAsync());
        Assert.Equal("openrouter-independent-secret", await other.GetAsync());
    }

    [Fact]
    public async Task Form_defaults_to_flex_and_persists_timeouts_tiers_rename_and_removal()
    {
        var (host, _) = await StartAsync();
        await using var lifetime = host;
        using var context = Context(host);
        var cut = Render(context);
        await cut.ClickAsync("#add-routercom-model");
        Assert.Equal("true", cut.Find("#routercom-model-form .flex-tier").GetAttribute("value"));
        await cut.ChangeAsync("#routercom-model-form .id", "router-flex");
        await cut.ChangeAsync("#routercom-model-form .upstream", "test-model");
        await cut.ChangeAsync("#routercom-model-form .tier-low", true);
        await cut.ChangeAsync("#routercom-model-form .fallback-timeout", "25");
        await cut.ClickAsync("#routercom-model-form .save");
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var model = await db.Models.FindAsync("router-flex");
            Assert.Equal(ProviderKind.RouterCom, model!.Provider);
            Assert.True(model.RouterCom!.AllowFlexTier);
            Assert.Equal(25, model.RouterCom.FallbackTimeoutSeconds);
            Assert.Equal(140, model.RouterCom.ProviderTimeoutSeconds);
            Assert.Equal(ModelTier.Low, (await db.TierMembers.SingleAsync(m => m.ModelId == model.Id)).Tier);
            Assert.False(db.Database.HasPendingModelChanges());
        }
        await cut.Row("model-settings", "router-flex").QuerySelector(".edit")!.ClickAsync(new());
        Assert.Equal("true", cut.Find("#routercom-model-form .flex-tier").GetAttribute("value"));
        await cut.ChangeAsync("#routercom-model-form .id", "router-standard");
        await cut.ChangeAsync("#routercom-model-form .flex-tier", "false");
        await cut.ClickAsync("#routercom-model-form .save");
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Null(await db.Models.FindAsync("router-flex"));
            var model = (await db.Models.FindAsync("router-standard"))!;
            Assert.False(model.RouterCom!.AllowFlexTier);
            // In-place edits to JSON options must be tracked too.
            model.RouterCom.FallbackTimeoutSeconds = 20;
            await db.SaveChangesAsync();
        }
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(20, (await db.Models.FindAsync("router-standard"))!.RouterCom!.FallbackTimeoutSeconds);
        await cut.Row("model-settings", "router-standard").QuerySelector(".remove")!.ClickAsync(new());
        await cut.Row("model-settings", "router-standard").QuerySelector(".confirm-remove")!.ClickAsync(new());
        await using var check = await factory.CreateDbContextAsync();
        Assert.Null(await check.Models.FindAsync("router-standard"));
    }

    [Fact]
    public async Task Invalid_timeouts_are_rejected_without_persisting_a_model()
    {
        await using var host = await RouterHost.StartAsync();
        var catalog = host.Services.GetRequiredService<CatalogSettings>();
        var input = new RouterComModelInput("invalid-router", "test-model", true, null, true, false, [ModelTier.Low],
            new() { FallbackTimeoutSeconds = 0 });
        await Assert.ThrowsAsync<SettingsException>(() => catalog.SaveRouterComModelAsync(input, null));
        await Assert.ThrowsAsync<SettingsException>(() => catalog.SaveRouterComModelAsync(input with { RouterCom = new() { ProviderTimeoutSeconds = -1 } }, null));
        Assert.DoesNotContain((await catalog.GetAsync()).Models, m => m.Id == input.Id);
    }
}
