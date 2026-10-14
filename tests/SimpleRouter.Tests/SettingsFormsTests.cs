using System.Net;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
}
