using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Dashboard;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.SystemOne;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.SettingsHarness;

namespace SimpleRouter.Tests;

/// <summary>SystemOne models managed through the settings page, and their effect on <c>/v1/systemone</c> without a restart.</summary>
public class SystemOneSettingsTests
{
    private static async Task<HttpStatusCode> DecideAsync(RouterHost host, string model)
    {
        var body = new JsonObject { ["model"] = model, ["state"] = "s", ["questions"] = new JsonObject() };
        using var response = await host.Client.PostAsync("/v1/systemone", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        return response.StatusCode;
    }

    private static async Task<RouterDbContext> DbAsync(RouterHost host) =>
        await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();

    [Fact]
    public async Task A_systemone_model_added_after_a_passing_connection_test_answers_decisions_with_its_key_and_upstream_id()
    {
        var (host, healthy) = await StartAsync();
        await using var _ = host;
        healthy.Add("decide.test");
        using var context = Context(host);
        var cut = Render(context);

        await cut.ClickAsync("#add-systemone-model");
        await cut.ChangeAsync("#systemone-model-form .id", "d3");
        await cut.ChangeAsync("#systemone-model-form .upstream", "vllm-d3");
        await cut.ChangeAsync("#systemone-model-form .base-url", "http://decide.test/v1");
        await cut.ChangeAsync("#systemone-model-form .api-key", "typed-decide-secret");
        await cut.ClickAsync("#systemone-model-form .test-connection");
        Assert.Contains("Connection OK", cut.Find("#systemone-model-form .test-result").TextContent);
        await cut.ClickAsync("#systemone-model-form .save");

        Assert.Empty(cut.FindAll("#systemone-model-form"));
        var row = cut.Row("model-settings", "d3").TextContent;
        Assert.Contains("systemone", row);
        Assert.DoesNotContain("typed-decide-secret", cut.Markup);
        Assert.Equal(HttpStatusCode.OK, await DecideAsync(host, "d3"));
        var sent = host.Local.Requests[^1];
        Assert.Equal("http://decide.test/v1/systemone", sent.Uri.ToString());
        Assert.Equal("Bearer typed-decide-secret", sent.Headers["authorization"]);
        Assert.Equal("vllm-d3", JsonNode.Parse(sent.Body)!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_systemone_model_can_be_renamed_with_its_usage_disabled_and_removed()
    {
        var (host, healthy) = await StartAsync();
        await using var _ = host;
        healthy.Add("decide.test");
        var settings = host.Services.GetRequiredService<CatalogSettings>();
        await settings.SaveSystemOneModelAsync(new SystemOneModelInput("d3", "vllm-d3", "http://decide.test/v1", true), existingId: null);
        Assert.Equal(HttpStatusCode.OK, await DecideAsync(host, "d3"));
        using var context = Context(host);
        var cut = Render(context);

        await cut.Row("model-settings", "d3").QuerySelector(".edit")!.ClickAsync(new());
        await cut.ChangeAsync("#systemone-model-form .id", "d3-renamed");
        await cut.ClickAsync("#systemone-model-form .save");
        var renamedServes = await DecideAsync(host, "d3-renamed");
        var oldIdServes = await DecideAsync(host, "d3");
        await cut.Row("model-settings", "d3-renamed").QuerySelector(".enabled")!.ChangeAsync(new() { Value = false });
        var disabledServes = await DecideAsync(host, "d3-renamed");
        await cut.Row("model-settings", "d3-renamed").QuerySelector(".remove")!.ClickAsync(new());
        await cut.Row("model-settings", "d3-renamed").QuerySelector(".confirm-remove")!.ClickAsync(new());

        Assert.Equal(HttpStatusCode.OK, renamedServes);
        Assert.Equal(HttpStatusCode.NotFound, oldIdServes);
        Assert.Equal(HttpStatusCode.NotFound, disabledServes);
        Assert.Empty(cut.FindAll("#model-settings [data-model='d3-renamed']"));
        await using var db = await DbAsync(host);
        Assert.Null(await db.Models.FindAsync("d3-renamed"));
        Assert.Equal(["d3-renamed", "d3-renamed"], await db.RequestUsages.Where(u => u.Provider == ProviderKind.SystemOne).Select(u => u.ModelId).ToListAsync());
    }

    [Fact]
    public async Task Capabilities_unticked_in_the_form_are_refused_at_once_and_shown_when_editing_again()
    {
        var (host, healthy) = await StartAsync();
        await using var _ = host;
        healthy.Add("decide.test");
        var settings = host.Services.GetRequiredService<CatalogSettings>();
        await settings.SaveSystemOneModelAsync(new SystemOneModelInput("jev", "~typesafe/jev-latest", "http://decide.test/v1", true), existingId: null);
        async Task<HttpStatusCode> BoxAsync()
        {
            var body = new JsonObject { ["model"] = "jev", ["state"] = "s", ["questions"] = new JsonObject { ["where"] = new JsonObject { ["type"] = "box", ["instructions"] = "the logo" } } };
            using var response = await host.Client.PostAsync("/v1/systemone", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            return response.StatusCode;
        }
        var before = await BoxAsync();
        using var context = Context(host);
        var cut = Render(context);

        await cut.Row("model-settings", "jev").QuerySelector(".edit")!.ClickAsync(new());
        await cut.ChangeAsync("#systemone-model-form .supports-box", false);
        await cut.ChangeAsync("#systemone-model-form .supports-videos", false);
        await cut.ClickAsync("#systemone-model-form .save");
        var after = await BoxAsync();
        await cut.Row("model-settings", "jev").QuerySelector(".edit")!.ClickAsync(new());

        Assert.Equal(HttpStatusCode.OK, before);
        Assert.Equal(HttpStatusCode.BadRequest, after);
        Assert.False(cut.Find("#systemone-model-form .supports-box").HasAttribute("checked"));
        Assert.True(cut.Find("#systemone-model-form .supports-images").HasAttribute("checked"));
        await Assert.ThrowsAsync<SettingsException>(() => settings.SaveSystemOneModelAsync(
            new SystemOneModelInput("jev", "~typesafe/jev-latest", "http://decide.test/v1", true, Supports: DecisionSupport.Images), existingId: "jev"));
        await using var db = await DbAsync(host);
        Assert.Equal(DecisionSupport.All & ~(DecisionSupport.Box | DecisionSupport.Videos),
            (await db.Models.FindAsync("jev"))!.Decisions);
    }

    [Fact]
    public async Task A_systemone_model_cannot_join_a_tier_and_is_not_offered_in_the_tier_picker()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;
        var settings = host.Services.GetRequiredService<CatalogSettings>();
        await settings.SaveSystemOneModelAsync(new SystemOneModelInput("d3", "vllm-d3", "http://decide.test/v1", true), existingId: null);
        using var context = Context(host);
        var cut = Render(context);

        var refused = await Assert.ThrowsAsync<SettingsException>(() => settings.AddToTierAsync(ModelTier.High, "d3"));
        var subscription = await Assert.ThrowsAsync<SettingsException>(() => settings.AddSubscriptionModelToTierAsync(ModelTier.High, ProviderKind.SystemOne, "d3"));

        Assert.Contains("cannot join a tier", refused.Message);
        Assert.Contains("model forms", subscription.Message);
        Assert.Empty(cut.FindAll("#tier-settings .add-existing option[value='d3']"));
        await using var db = await DbAsync(host);
        Assert.False(await db.TierMembers.AnyAsync(m => m.ModelId == "d3"));
    }
}
