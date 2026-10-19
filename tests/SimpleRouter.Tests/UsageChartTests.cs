using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SimpleRouter.Dashboard;
using SimpleRouter.Dashboard.Components.Panels;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

/// <summary>The dashboard's usage charts: recorded requests bucketed per model over the picked range.</summary>
public class UsageChartTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 15, 30, 0, TimeSpan.Zero);

    private static async Task<(RouterHost Host, DashboardData Data)> StartAsync()
    {
        var host = await RouterHost.StartAsync();
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RequestUsages.AddRange(
                Row("claude-a", new(2026, 10, 9, 23, 59, 0, TimeSpan.Zero), 99_000),
                Row("claude-a", new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), 5_000),
                Row("claude-a", new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero), 10_000),
                Row("codex-c", new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero), 2_000));
            await db.SaveChangesAsync();
        }
        var data = new DashboardData(factory, host.Services.GetRequiredService<ModelSelector>(),
            host.Services.GetRequiredService<SimpleRouter.Usage.SubscriptionUsage>(), new FakeTimeProvider(Now));
        return (host, data);
    }

    private static RequestUsage Row(string model, DateTimeOffset at, long costMicros) => new()
    {
        At = at, ModelId = model, Provider = ProviderKind.Claude, Tier = ModelTier.High, Status = 200,
        InputTokens = 100, OutputTokens = 20, CostUsdMicros = costMicros,
    };

    [Fact]
    public async Task The_week_buckets_requests_and_cost_per_model_by_day_and_leaves_out_older_rows()
    {
        var (host, data) = await StartAsync();
        await using var _ = host;

        var chart = await data.UsageChartAsync(UsageRange.Week, CancellationToken.None);

        Assert.Equal(7, chart.Buckets.Length);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), chart.Buckets[0]);
        Assert.Equal(["claude-a", "codex-c"], chart.Series.Select(s => s.ModelId));
        Assert.Equal([0, 0, 0, 0, 0, 1, 1], chart.Series[0].Requests);
        Assert.Equal([0, 0, 0, 0, 0, 5_000, 10_000], chart.Series[0].CostUsdMicros);
        Assert.Equal([0, 0, 0, 0, 0, 0, 1], chart.Series[1].Requests);
        Assert.Equal((3, 360, 17_000), (chart.Requests, chart.Tokens, chart.CostUsdMicros));
    }

    [Fact]
    public async Task The_day_buckets_by_hour_over_the_last_24_hours()
    {
        var (host, data) = await StartAsync();
        await using var _ = host;

        var chart = await data.UsageChartAsync(UsageRange.Day, CancellationToken.None);

        Assert.Equal(24, chart.Buckets.Length);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 16, 0, 0, TimeSpan.Zero), chart.Buckets[0]);
        Assert.Equal(1, chart.Series[0].Requests[17]);
        Assert.Equal(1, chart.Series[1].Requests[18]);
        Assert.Equal((2, 12_000L), (chart.Requests, chart.CostUsdMicros));
    }

    [Fact]
    public async Task Unknown_cost_is_counted_and_the_panel_labels_totals_incomplete()
    {
        var (host, data) = await StartAsync();
        await using var _ = host;
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync())
        {
            var row = Row("auto", Now, 0);
            row.Provider = ProviderKind.OpenRouter;
            row.CostSource = "unknown";
            db.RequestUsages.Add(row);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, (await data.UsageChartAsync(UsageRange.Week, default)).UnknownCostRequests);
        using var context = new BunitContext();
        context.Services.AddLogging();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton(host.Services.GetRequiredService<DashboardEvents>());
        context.Services.AddSingleton(data);
        var cut = context.Render<UsageCharts>();
        cut.WaitForAssertion(() => Assert.Contains("totals are incomplete", cut.Markup), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task The_panel_draws_both_charts_with_a_legend_and_redraws_for_a_picked_range()
    {
        var (host, data) = await StartAsync();
        await using var _ = host;
        using var context = new BunitContext();
        context.Services.AddLogging();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton(host.Services.GetRequiredService<DashboardEvents>());
        context.Services.AddSingleton(data);

        var cut = context.Render<UsageCharts>();

        cut.WaitForAssertion(() => Assert.Equal("3", cut.Find("#usage-requests").TextContent), TimeSpan.FromSeconds(10));
        Assert.Equal("$0.02", cut.Find("#usage-cost").TextContent);
        Assert.Equal(["claude-a", "codex-c"], cut.FindAll(".legend li").Select(li => li.TextContent));
        Assert.Equal(3, cut.FindAll("#model-usage rect[fill]").Count);
        Assert.Equal(3, cut.FindAll("#model-spend rect[fill]").Count);

        cut.FindAll(".segmented button").Single(b => b.TextContent == "24h").Click();

        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("#usage-requests").TextContent), TimeSpan.FromSeconds(10));
        Assert.Equal(2, cut.FindAll("#model-usage rect[fill]").Count);
    }
}
