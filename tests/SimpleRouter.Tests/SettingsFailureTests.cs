using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Dashboard;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.SettingsHarness;

namespace SimpleRouter.Tests;

/// <summary>Refused and failing settings edits: limits on editable values, atomic writes, and errors kept inside the page.</summary>
public class SettingsFailureTests
{
    private static async Task<RouterDbContext> DbAsync(RouterHost host) =>
        await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();

    private static async Task ExecuteAsync(RouterHost host, string sql)
    {
        await using var db = await DbAsync(host);
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    [Fact]
    public async Task Alias_patterns_are_limited_in_length_and_wildcards()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var catalog = host.Services.GetRequiredService<CatalogSettings>();

        await Assert.ThrowsAsync<SettingsException>(() => catalog.SaveAliasesAsync([new AliasRow(new string('a', 201), ModelTier.Low)]));
        await Assert.ThrowsAsync<SettingsException>(() => catalog.SaveAliasesAsync([new AliasRow("*a*a*a*a*b", ModelTier.Low)]));
        await using (var db = await DbAsync(host))
        {
            Assert.Equal(["*opus*"], await db.TierAliases.Select(a => a.Pattern).ToListAsync());
        }

        var longest = new string('a', 200);
        await catalog.SaveAliasesAsync([new AliasRow(longest, ModelTier.Low), new AliasRow("*a*a*a*b", ModelTier.Mid)]);
        await using var check = await DbAsync(host);
        Assert.Equal([longest, "*a*a*a*b"], await check.TierAliases.OrderBy(a => a.Position).Select(a => a.Pattern).ToListAsync());
    }

    [Fact]
    public async Task A_failed_alias_save_keeps_the_previous_aliases()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        await ExecuteAsync(host, "CREATE TRIGGER refuse_alias BEFORE INSERT ON TierAliases WHEN NEW.Pattern = 'refused' BEGIN SELECT RAISE(ABORT, 'refused'); END;");

        await Assert.ThrowsAsync<DbUpdateException>(() => host.Services.GetRequiredService<CatalogSettings>()
            .SaveAliasesAsync([new AliasRow("*custom*", ModelTier.Mid), new AliasRow("refused", ModelTier.Low)]));

        await using var db = await DbAsync(host);
        Assert.Equal(["*opus*"], await db.TierAliases.Select(a => a.Pattern).ToListAsync());
    }

    [Fact]
    public async Task A_failed_local_model_removal_keeps_its_tier_membership_and_price()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var catalog = host.Services.GetRequiredService<CatalogSettings>();
        await catalog.AddToTierAsync(ModelTier.Mid, "local-seeded");
        await catalog.SavePriceAsync(new PriceRow("local-seeded", 1, 2, 0, 0, 0));
        await ExecuteAsync(host, "CREATE TRIGGER refuse_model_delete BEFORE DELETE ON Models BEGIN SELECT RAISE(ABORT, 'refused'); END;");

        await Assert.ThrowsAsync<DbUpdateException>(() => catalog.RemoveLocalModelAsync("local-seeded"));

        await using var db = await DbAsync(host);
        Assert.NotNull(await db.Models.FindAsync("local-seeded"));
        Assert.True(await db.TierMembers.AnyAsync(m => m.ModelId == "local-seeded" && m.Tier == ModelTier.Mid));
        Assert.Equal(2, (await db.ModelPrices.FindAsync("local-seeded"))!.OutputPerMTok);
    }

    [Fact]
    public async Task A_price_above_the_limit_is_refused()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var catalog = host.Services.GetRequiredService<CatalogSettings>();

        await Assert.ThrowsAsync<SettingsException>(() => catalog.SavePriceAsync(new PriceRow("codex-c", 0, 1_000_001, 0, 0, 0)));
        await using (var db = await DbAsync(host))
        {
            Assert.Null(await db.ModelPrices.FindAsync("codex-c"));
        }

        await catalog.SavePriceAsync(new PriceRow("codex-c", 0, 1_000_000, 0, 0, 0));
        await using var check = await DbAsync(host);
        Assert.Equal(1_000_000, (await check.ModelPrices.FindAsync("codex-c"))!.OutputPerMTok);
    }

    [Fact]
    public async Task A_refused_enable_toggle_puts_the_checkbox_back_to_its_stored_state()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        using var context = Context(host);
        var cut = Render(context);
        await ExecuteAsync(host, "CREATE TRIGGER refuse_model_update BEFORE UPDATE ON Models BEGIN SELECT RAISE(ABORT, 'refused'); END;");
        var shown = new List<bool>();
        cut.OnMarkupUpdated += (_, _) => shown.Add(cut.Find("#model-settings [data-model='claude-a'] .enabled").HasAttribute("checked"));

        await cut.ChangeAsync("#model-settings [data-model='claude-a'] .enabled", false);

        Assert.Contains("could not be saved", cut.Find("#model-settings .form-error").TextContent);
        var refused = shown.IndexOf(false);
        Assert.True(refused >= 0, "The refused value was never rendered, so the browser would keep showing it.");
        Assert.Contains(true, shown.Skip(refused));
        Assert.True(cut.Find("#model-settings [data-model='claude-a'] .enabled").HasAttribute("checked"));
        await using var db = await DbAsync(host);
        Assert.True((await db.Models.FindAsync("claude-a"))!.Enabled);
    }

    [Fact]
    public async Task An_unreadable_stored_key_fails_the_connection_test_inside_the_form()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        await ExecuteAsync(host, "UPDATE Models SET ProtectedApiKey = 'not-a-protected-value' WHERE Id = 'local-seeded';");
        using var context = Context(host);
        var cut = Render(context);

        await cut.Row("model-settings", "local-seeded").QuerySelector(".edit")!.ClickAsync(new());
        var probes = host.Local.Requests.Count;
        await cut.ClickAsync("#local-model-form .test-connection");

        Assert.Equal("The connection could not be tested; see the server log.", cut.Find("#local-model-form .test-result").TextContent);
        Assert.Equal(probes, host.Local.Requests.Count);
    }
}
