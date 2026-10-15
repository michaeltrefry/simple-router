using System.Net;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Dashboard;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.SettingsHarness;

namespace SimpleRouter.Tests;

/// <summary>Each settings form, driven through the rendered page, and its effect on the next request without a restart.</summary>
public class SettingsFormsTests
{
    private static async Task<RouterDbContext> DbAsync(RouterHost host) =>
        await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();

    [Fact]
    public async Task A_local_model_added_after_a_passing_connection_test_serves_the_next_new_session_of_its_tier()
    {
        var (host, healthy) = await StartAsync();
        await using var _ = host;
        healthy.Add("new-local.test");
        using var context = Context(host);
        var cut = Render(context);
        Assert.Equal("upstream-a", await ServedByAsync(host, Message("before", "mid")));

        await cut.ClickAsync("#add-local-model");
        await cut.ChangeAsync("#local-model-form .id", "new-local");
        await cut.ChangeAsync("#local-model-form .upstream", "served-new");
        await cut.ChangeAsync("#local-model-form .base-url", "http://new-local.test/v1");
        await cut.ChangeAsync("#local-model-form .api-key", "typed-local-secret");
        await cut.ChangeAsync("#local-model-form .context-window", "32000");
        await cut.ChangeAsync("#local-model-form .tier-mid", true);
        await cut.ClickAsync("#local-model-form .test-connection");
        Assert.Contains("Connection OK", cut.Find("#local-model-form .test-result").TextContent);
        Assert.Equal("Bearer typed-local-secret", host.Local.Requests[^1].Headers["authorization"]);
        await cut.ClickAsync("#local-model-form .save");

        Assert.Empty(cut.FindAll("#local-model-form"));
        Assert.Contains("mid", cut.Row("model-settings", "new-local").TextContent);
        Assert.DoesNotContain("typed-local-secret", cut.Markup);
        Assert.Equal("new-local.test", await ServedByAsync(host, Message("after", "mid")));
        var chat = host.Local.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/chat/completions"));
        Assert.Equal("Bearer typed-local-secret", chat.Headers["authorization"]);
        Assert.Contains("\"served-new\"", System.Text.Encoding.UTF8.GetString(chat.Body));
        Assert.Equal("upstream-a", await ServedByAsync(host, Message("before", "mid")));
    }

    [Fact]
    public async Task The_local_form_refuses_a_non_http_base_url_and_an_invalid_effort_mapping_and_reports_a_failing_endpoint()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        using var context = Context(host);
        var cut = Render(context);
        await cut.ClickAsync("#add-local-model");
        await cut.ChangeAsync("#local-model-form .id", "bad-local");
        await cut.ChangeAsync("#local-model-form .upstream", "served");

        await cut.ChangeAsync("#local-model-form .base-url", "ftp://bad.test/v1");
        await cut.ClickAsync("#local-model-form .save");
        Assert.Contains("http:// or https://", cut.Find("#local-model-form [role=alert]").TextContent);

        await cut.ChangeAsync("#local-model-form .base-url", "http://down.test/v1");
        await cut.ClickAsync("#local-model-form .test-connection");
        Assert.Contains("Connection failed: GET http://down.test/v1/models answered HTTP 503", cut.Find("#local-model-form .test-result").TextContent);

        await cut.ChangeAsync("#local-model-form .effort-mode", "chat_template_kwargs");
        await cut.ChangeAsync("#local-model-form .effort-levels", "none, high");
        await cut.ChangeAsync("#local-model-form .effort-kwargs", """{"high":{"enable_thinking":true}}""");
        await cut.ClickAsync("#local-model-form .save");
        Assert.Contains("lists none without kwargs", cut.Find("#local-model-form [role=alert]").TextContent);

        await using var db = await DbAsync(host);
        Assert.Null(await db.Models.FindAsync("bad-local"));
    }

    [Fact]
    public async Task Editing_a_local_model_never_renders_its_stored_key_and_keeps_or_clears_it_as_asked()
    {
        var (host, healthy) = await StartAsync();
        await using var _host = host;
        healthy.Add("seeded.test");
        using var context = Context(host);
        var cut = Render(context);

        await cut.Row("model-settings", "local-seeded").QuerySelector(".edit")!.ClickAsync(new());
        Assert.Equal("(set)", cut.Find("#local-model-form .api-key-state").TextContent);
        Assert.DoesNotContain("seeded-secret-key", cut.Markup);
        await cut.ClickAsync("#local-model-form .test-connection");
        Assert.Equal("Bearer seeded-secret-key", host.Local.Requests[^1].Headers["authorization"]);
        await cut.ChangeAsync("#local-model-form .tier-low", true);
        await cut.ChangeAsync("#local-model-form .effort-mode", "reasoning_effort");
        await cut.ChangeAsync("#local-model-form .effort-levels", "high, low");
        await cut.ClickAsync("#local-model-form .save");

        Assert.Equal("seeded.test", await ServedByAsync(host, Message("kept-key", "low")));
        Assert.Equal("Bearer seeded-secret-key", host.Local.Requests[^1].Headers["authorization"]);
        await using (var db = await DbAsync(host))
        {
            var model = await db.Models.AsNoTracking().SingleAsync(m => m.Id == "local-seeded");
            Assert.Equal([EffortLevel.Low, EffortLevel.High], model.Effort!.Levels);
            Assert.Equal(EffortMode.ReasoningEffort, model.Effort.Mode);
        }

        await cut.Row("model-settings", "local-seeded").QuerySelector(".edit")!.ClickAsync(new());
        await cut.ChangeAsync("#local-model-form .clear-api-key", true);
        await cut.ClickAsync("#local-model-form .save");
        await cut.Row("model-settings", "local-seeded").QuerySelector(".edit")!.ClickAsync(new());
        Assert.Equal("(not set)", cut.Find("#local-model-form .api-key-state").TextContent);
        Assert.Equal("seeded.test", await ServedByAsync(host, Message("cleared-key", "low")));
        Assert.False(host.Local.Requests[^1].Headers.ContainsKey("authorization"));
    }

    [Fact]
    public async Task Removing_a_local_model_takes_it_out_of_routing_and_the_catalog()
    {
        var (host, healthy) = await StartAsync();
        await using var _host = host;
        healthy.Add("seeded.test");
        using var context = Context(host);
        var cut = Render(context);
        await cut.Row("model-settings", "local-seeded").QuerySelector(".edit")!.ClickAsync(new());
        await cut.ChangeAsync("#local-model-form .tier-mid", true);
        await cut.ClickAsync("#local-model-form .save");
        Assert.Equal("seeded.test", await ServedByAsync(host, Message("pinned-local", "mid")));

        await cut.Row("model-settings", "local-seeded").QuerySelector(".remove")!.ClickAsync(new());
        await cut.Row("model-settings", "local-seeded").QuerySelector(".confirm-remove")!.ClickAsync(new());

        Assert.Empty(cut.FindAll("#model-settings [data-model='local-seeded']"));
        Assert.Equal("upstream-a", await ServedByAsync(host, Message("pinned-local", "mid")));
        await using var db = await DbAsync(host);
        Assert.Null(await db.Models.FindAsync("local-seeded"));
        Assert.False(await db.TierMembers.AnyAsync(m => m.ModelId == "local-seeded"));
    }

    [Fact]
    public async Task Disabling_a_pinned_model_re_pins_its_session_within_the_tier()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        using var context = Context(host);
        var cut = Render(context);
        Assert.Equal("upstream-a", await ServedByAsync(host, Message("pinned", "high")));

        await cut.ChangeAsync("#model-settings [data-model='claude-a'] .enabled", false);

        Assert.Contains("claude-a disabled.", cut.Find("#model-settings .form-notice").TextContent);
        Assert.Equal("upstream-b", await ServedByAsync(host, Message("pinned", "high")));
        Assert.Equal("upstream-b", await ServedByAsync(host, Message("brand-new", "high")));
        using var exhausted = await host.Client.SendAsync(Message("mid-session", "mid"));
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);
    }

    [Fact]
    public async Task Tier_edits_add_a_picked_subscription_model_and_remove_members_for_the_next_new_session()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        await using (var db = await DbAsync(host))
        {
            db.ProviderModelLists.Add(new ProviderModelList
            {
                Provider = ProviderKind.Claude, ModelIds = ["upstream-a", "upstream-b", "claude-picked"], FetchedAt = DateTimeOffset.UtcNow, CheckedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        using var context = Context(host);
        var cut = Render(context);
        Assert.Equal("upstream-a", await ServedByAsync(host, Message("old-session", "high")));

        Assert.Equal(["upstream-a", "upstream-b", "claude-picked"], cut.FindAll("#discovered-claude option").Select(o => o.GetAttribute("value")));
        await cut.ChangeAsync("#tier-settings [data-tier='high'] .subscription-provider", "Codex");
        Assert.Contains("no list fetched yet", cut.Find("#tier-settings [data-tier='high'] .subscription-model").GetAttribute("placeholder"));
        await cut.ChangeAsync("#tier-settings [data-tier='high'] .subscription-provider", "Claude");
        Assert.Equal("pick a claude model", cut.Find("#tier-settings [data-tier='high'] .subscription-model").GetAttribute("placeholder"));
        Assert.Equal("discovered-claude", cut.Find("#tier-settings [data-tier='high'] .subscription-model").GetAttribute("list"));
        await cut.ChangeAsync("#tier-settings [data-tier='high'] .subscription-model", "claude-picked");
        await cut.ClickAsync("#tier-settings [data-tier='high'] .add-subscription-button");
        await cut.Find("#tier-settings [data-tier='high'] [data-model='claude-a'] .remove").ClickAsync(new());
        await cut.Find("#tier-settings [data-tier='high'] [data-model='claude-b'] .remove").ClickAsync(new());

        Assert.Equal(["claude-picked"], cut.FindAll("#tier-settings [data-tier='high'] li").Select(li => li.GetAttribute("data-model")));
        Assert.Equal("claude-picked", await ServedByAsync(host, Message("new-session", "high")));
        Assert.Equal("claude-picked", await ServedByAsync(host, Message("old-session", "high")));

        await cut.ChangeAsync("#tier-settings [data-tier='low'] .add-existing", "claude-b");
        await cut.ClickAsync("#tier-settings [data-tier='low'] .add-existing-button");
        Assert.Contains(cut.FindAll("#tier-settings [data-tier='low'] li"), li => li.GetAttribute("data-model") == "claude-b");
        Assert.Equal("upstream-b", await ServedByAsync(host, Message("low-session", "low")));
    }

    [Fact]
    public async Task Alias_and_default_tier_edits_route_the_next_request_and_an_empty_pattern_is_refused()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        using var context = Context(host);
        var cut = Render(context);

        await cut.ChangeAsync("#alias-settings .default-tier", "low");
        await cut.ClickAsync("#alias-settings .add-alias");
        await cut.ChangeAsync("#alias-settings .alias-row:last-child .pattern", "*custom*");
        await cut.ChangeAsync("#alias-settings .alias-row:last-child .alias-tier", "Mid");
        await cut.ClickAsync("#alias-settings .alias-row:last-child .up");
        await cut.ClickAsync("#alias-settings .save-aliases");

        Assert.Contains("Aliases saved.", cut.Find("#alias-settings .form-notice").TextContent);
        using (var custom = await host.Client.SendAsync(Message("alias-1", null, model: "my-custom-opus")))
        {
            Assert.Equal("mid", custom.Headers.GetValues("X-Weave-Model-Class").Single());
        }
        using (var fallback = await host.Client.SendAsync(Message("alias-2", null, model: "unmatched")))
        {
            Assert.Equal("low", fallback.Headers.GetValues("X-Weave-Model-Class").Single());
        }

        await cut.ClickAsync("#alias-settings .add-alias");
        await cut.ClickAsync("#alias-settings .save-aliases");
        Assert.Contains("cannot be empty", cut.Find("#alias-settings .form-error").TextContent);
        await using var db = await DbAsync(host);
        Assert.Equal(["*custom*", "*opus*"], await db.TierAliases.OrderBy(a => a.Position).Select(a => a.Pattern).ToListAsync());
    }

    [Fact]
    public async Task A_price_edit_prices_the_next_recorded_request_and_a_negative_price_is_refused()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        using var context = Context(host);
        var cut = Render(context);

        await cut.Row("price-settings", "claude-a").QuerySelector(".price-0")!.ChangeAsync(new() { Value = "7" });
        await cut.Row("price-settings", "claude-a").QuerySelector(".price-1")!.ChangeAsync(new() { Value = "11" });
        await cut.Row("price-settings", "claude-a").QuerySelector(".save-price")!.ClickAsync(new());
        Assert.Contains("Prices for claude-a saved.", cut.Find("#price-settings .form-notice").TextContent);
        Assert.Equal("upstream-a", await ServedByAsync(host, Message("priced", "high")));

        long? cost = null;
        var deadline = DateTime.UtcNow + Wait;
        while (cost is null && DateTime.UtcNow < deadline)
        {
            await using var db = await DbAsync(host);
            cost = (await db.RequestUsages.FirstOrDefaultAsync(u => u.ClientSessionId == "priced"))?.CostUsdMicros;
            await Task.Delay(50);
        }
        Assert.Equal(12 * 7 + 34 * 11, cost);

        await cut.Row("price-settings", "codex-c").QuerySelector(".price-1")!.ChangeAsync(new() { Value = "-1" });
        await cut.Row("price-settings", "codex-c").QuerySelector(".save-price")!.ClickAsync(new());
        Assert.Contains("cannot be negative", cut.Find("#price-settings .form-error").TextContent);
        await using var check = await DbAsync(host);
        Assert.Null(await check.ModelPrices.FindAsync("codex-c"));
    }

    private static async Task<long> CursorAsync(RouterHost host, ModelTier tier)
    {
        await using var db = await DbAsync(host);
        return (await db.TierCursors.AsNoTracking().SingleAsync(c => c.Tier == tier)).Next;
    }

    [Fact]
    public async Task Renaming_a_local_model_moves_its_tiers_price_cooldown_usage_and_pins_so_its_session_stays_on_it()
    {
        var (host, healthy) = await StartAsync();
        await using var _host = host;
        healthy.Add("seeded.test");
        using var context = Context(host);
        var cut = Render(context);
        await cut.Row("model-settings", "local-seeded").QuerySelector(".edit")!.ClickAsync(new());
        await cut.ChangeAsync("#local-model-form .tier-mid", true);
        await cut.ClickAsync("#local-model-form .save");
        var settings = host.Services.GetRequiredService<CatalogSettings>();
        await settings.AddToTierAsync(ModelTier.Mid, "claude-b");
        await settings.SavePriceAsync(new PriceRow("local-seeded", 1, 2, 0.1m, 0.2m, 0.3m));
        Assert.Equal("seeded.test", await ServedByAsync(host, Message("pinned-local", "mid")));
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            await using var db = await DbAsync(host);
            if (await db.RequestUsages.AnyAsync(u => u.ModelId == "local-seeded"))
            {
                db.ModelCooldowns.Add(new ModelCooldown { ModelId = "local-seeded", Until = DateTimeOffset.UtcNow.AddHours(-1), Reason = FaultKind.Limited });
                // Left over for an ID that is not in the catalog; the moved rows replace them.
                db.ModelCooldowns.Add(new ModelCooldown { ModelId = "local-renamed", Until = DateTimeOffset.UtcNow.AddHours(-2), Reason = FaultKind.Overloaded });
                db.ModelPrices.Add(new ModelPrice { ModelId = "local-renamed", InputPerMTok = 9 });
                await db.SaveChangesAsync();
                break;
            }
            await Task.Delay(50);
        }
        var cursor = await CursorAsync(host, ModelTier.Mid);

        await cut.Row("model-settings", "local-seeded").QuerySelector(".edit")!.ClickAsync(new());
        Assert.False(cut.Find("#local-model-form .id").HasAttribute("disabled"));
        await cut.ChangeAsync("#local-model-form .id", "  local-renamed  ");
        await cut.ClickAsync("#local-model-form .save");

        Assert.Empty(cut.FindAll("#local-model-form"));
        Assert.Empty(cut.FindAll("#model-settings [data-model='local-seeded']"));
        Assert.Contains("mid", cut.Row("model-settings", "local-renamed").TextContent);
        await using (var db = await DbAsync(host))
        {
            Assert.Null(await db.Models.FindAsync("local-seeded"));
            Assert.Equal(["claude-a", "local-renamed", "claude-b"],
                await db.TierMembers.Where(m => m.Tier == ModelTier.Mid).OrderBy(m => m.Position).Select(m => m.ModelId).ToListAsync());
            var price = await db.ModelPrices.AsNoTracking().SingleAsync(p => p.ModelId == "local-renamed");
            Assert.Equal((1m, 2m, 0.3m), (price.InputPerMTok, price.OutputPerMTok, price.CacheWrite1hPerMTok));
            Assert.False(await db.ModelPrices.AnyAsync(p => p.ModelId == "local-seeded"));
            var cooldown = await db.ModelCooldowns.AsNoTracking().SingleAsync();
            Assert.Equal(("local-renamed", FaultKind.Limited), (cooldown.ModelId, cooldown.Reason));
            Assert.Equal(["local-renamed"], await db.SessionPins.Where(p => p.Tier == ModelTier.Mid).Select(p => p.ModelId).ToListAsync());
            Assert.NotEmpty(await db.RequestUsages.Where(u => u.ModelId == "local-renamed").ToListAsync());
            Assert.False(await db.RequestUsages.AnyAsync(u => u.ModelId == "local-seeded"));
        }

        Assert.Equal("seeded.test", await ServedByAsync(host, Message("pinned-local", "mid")));
        Assert.Equal("Bearer seeded-secret-key", host.Local.Requests[^1].Headers["authorization"]);
        Assert.Equal(cursor, await CursorAsync(host, ModelTier.Mid));
    }

    [Fact]
    public async Task A_rename_to_a_taken_invalid_or_subscription_id_is_refused_and_subscription_ids_cannot_be_renamed()
    {
        var (host, healthy) = await StartAsync();
        await using var _host = host;
        healthy.Add("seeded.test");
        await using (var db = await DbAsync(host))
        {
            db.ProviderModelLists.Add(new ProviderModelList { Provider = ProviderKind.Claude, ModelIds = ["listed-sub"], CheckedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var context = Context(host);
        var cut = Render(context);
        await cut.Row("model-settings", "local-seeded").QuerySelector(".edit")!.ClickAsync(new());

        foreach (var (id, error) in new[]
        {
            ("CLAUDE-B", "already exists"),
            ("bad id!", "must be 1-200"),
            ("   ", "must be 1-200"),
            ("Listed-Sub", "subscription model ID"),
        })
        {
            await cut.ChangeAsync("#local-model-form .id", id);
            await cut.ClickAsync("#local-model-form .save");
            Assert.Contains(error, cut.Find("#local-model-form [role=alert]").TextContent);
        }

        var settings = host.Services.GetRequiredService<CatalogSettings>();
        var sub = new LocalModelInput("renamed-sub", "upstream-a", "http://seeded.test/v1", null, true, false, true, null, [ModelTier.High]);
        var refused = await Assert.ThrowsAsync<SettingsException>(() => settings.SaveLocalModelAsync(sub, existingId: "claude-a"));
        Assert.Contains("not a local model", refused.Message);

        await using var check = await DbAsync(host);
        Assert.Equal(["claude-a", "claude-b", "codex-c", "local-seeded"], await check.Models.OrderBy(m => m.Id).Select(m => m.Id).ToListAsync());
        Assert.Equal(["claude-a", "claude-b"], await check.TierMembers.Where(m => m.Tier == ModelTier.High).OrderBy(m => m.Position).Select(m => m.ModelId).ToListAsync());
    }
}
