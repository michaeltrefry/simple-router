using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Dashboard;
using SimpleRouter.Dashboard.Components;
using SimpleRouter.Dashboard.Components.Panels;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class RequestHistoryTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static RequestUsage Usage(string? key = "pin-a", ModelTier? tier = ModelTier.High, string model = "entry-a") => new()
    {
        At = DateTimeOffset.UtcNow, SessionKey = key, ClientSessionId = "shared-client", Tier = tier,
        ModelId = model, Provider = ProviderKind.OpenRouter, Status = 200,
    };

    private static async Task SeedAsync(RouterHost host, params RequestUsage[] usages)
    {
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        db.RequestUsages.AddRange(usages);
        await db.SaveChangesAsync();
    }

    private static BunitContext Dashboard(RouterHost host)
    {
        var context = new BunitContext();
        context.Services.AddLogging();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton(host.Services.GetRequiredService<DashboardEvents>());
        context.Services.AddSingleton(host.Services.GetRequiredService<DashboardData>());
        return context;
    }

    private static Dictionary<string, string> Metadata(IRenderedComponent<RequestDetails> cut, string id) =>
        cut.FindAll($"#{id} dt").ToDictionary(e => e.TextContent, e => e.NextElementSibling!.TextContent);

    [Fact]
    public async Task Exact_key_and_tier_history_survives_repinning_and_never_guesses_from_shared_client_or_model()
    {
        await using var host = await RouterHost.StartAsync();
        var old = Usage();
        var repinned = Usage(model: "entry-b");
        await SeedAsync(host, old, Usage("pin-b"), Usage(tier: ModelTier.Low), Usage(null), Usage(tier: null), repinned);
        var dashboard = host.Services.GetRequiredService<DashboardData>();

        var page = await dashboard.PinRequestsAsync("pin-a", ModelTier.High, null, CancellationToken.None);

        Assert.Equal(new[] { repinned.Id, old.Id }, page.Rows.Select(r => r.Id));
        Assert.Equal(new[] { "entry-b", "entry-a" }, page.Rows.Select(r => r.ModelId));
        Assert.Null(page.NextBeforeId);
        Assert.Null(await dashboard.PinAsync("pin-a", ModelTier.High, CancellationToken.None));
        Assert.Empty((await dashboard.PinRequestsAsync("PIN-A", ModelTier.High, null, CancellationToken.None)).Rows);
    }

    [Fact]
    public async Task Id_cursor_reaches_history_beyond_fifty_without_duplicates_when_new_rows_arrive()
    {
        await using var host = await RouterHost.StartAsync();
        var rows = Enumerable.Range(0, 113).Select(_ => Usage()).ToArray();
        await SeedAsync(host, rows);
        var dashboard = host.Services.GetRequiredService<DashboardData>();
        var first = await dashboard.PinRequestsAsync("pin-a", ModelTier.High, null, CancellationToken.None);
        await SeedAsync(host, Usage());
        var second = await dashboard.PinRequestsAsync("pin-a", ModelTier.High, first.NextBeforeId, CancellationToken.None);
        var third = await dashboard.PinRequestsAsync("pin-a", ModelTier.High, second.NextBeforeId, CancellationToken.None);

        Assert.Equal(50, first.Rows.Count);
        Assert.Equal(50, second.Rows.Count);
        Assert.Equal(13, third.Rows.Count);
        Assert.Null(third.NextBeforeId);
        Assert.Equal(rows.Reverse().Select(r => r.Id), first.Rows.Concat(second.Rows).Concat(third.Rows).Select(r => r.Id));
    }

    [Fact]
    public async Task Monitoring_links_use_the_exact_pin_identity_and_persisted_request_id()
    {
        await using var host = await RouterHost.StartAsync();
        var row = Usage("key/with+symbols&other=value");
        await SeedAsync(host, row);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync())
        {
            db.SessionPins.Add(new SessionPin { SessionKey = row.SessionKey!, Tier = ModelTier.High, ModelId = "entry-a", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var context = Dashboard(host);
        var pins = context.Render<SessionPins>();
        var recent = context.Render<RecentRequests>();

        pins.WaitForAssertion(() => Assert.Equal(DashboardFormat.PinUrl(row.SessionKey!, ModelTier.High), pins.Find("#session-pins a").GetAttribute("href")), Wait);
        recent.WaitForAssertion(() => Assert.Equal(DashboardFormat.RequestUrl(row.Id), recent.Find("#recent-requests a").GetAttribute("href")), Wait);
        Assert.Contains("key%2Fwith%2Bsymbols%26other%3Dvalue", pins.Find("#session-pins a").GetAttribute("href"));
    }

    [Fact]
    public async Task Detail_separates_failed_stream_from_http_and_displays_safe_model_usage_and_correlation_metadata()
    {
        await using var host = await RouterHost.StartAsync();
        var row = Usage();
        row.UpstreamModelId = "openrouter/auto";
        row.ResolvedModelId = "vendor/resolved-model";
        row.GenerationId = "generation-test";
        row.IsStreaming = true;
        row.Outcome = "failed";
        row.FailureKind = "incomplete_stream";
        row.FailureDetail = "Upstream stream ended before completion.";
        row.StopReason = "max_tokens";
        row.Effort = "high";
        row.LatencyMs = 1234;
        row.InputTokens = 100;
        row.OutputTokens = 200;
        row.CacheReadTokens = 30;
        row.CacheWriteTokens = 40;
        row.CacheWrite1hTokens = 10;
        row.CostUsdMicros = 123456;
        row.CostSource = "reported";
        await SeedAsync(host, row);
        using var context = Dashboard(host);
        var cut = context.Render<RequestDetails>(p => p.Add(c => c.Id, row.Id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#request-result")), Wait);

        var result = Metadata(cut, "request-result");
        Assert.Equal("Failed", result["Outcome"]);
        Assert.Equal("200", result["HTTP status"]);
        Assert.Equal("Streaming", result["Response mode"]);
        Assert.Equal(row.FailureDetail, result["Failure detail"]);
        Assert.Equal("max_tokens", result["Stop reason"]);
        var routing = Metadata(cut, "request-routing");
        Assert.Equal("entry-a", routing["Configured router entry"]);
        Assert.Equal("openrouter/auto", routing["Configured upstream snapshot"]);
        Assert.Equal("vendor/resolved-model", routing["Actual resolved upstream model"]);
        Assert.Equal("generation-test", routing["Generation ID"]);
        Assert.Equal("pin-a", routing["Routing session key"]);
        var usage = Metadata(cut, "request-usage");
        Assert.Equal("30", usage["Cache-read tokens"]);
        Assert.Equal("40", usage["Cache-write tokens (total)"]);
        Assert.Equal("10", usage["Cache-write tokens (1-hour subset)"]);
        Assert.Equal("$0.123456", usage["Cost (USD)"]);
        Assert.Equal("reported", usage["Cost provenance"]);
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == DashboardFormat.PinUrl("pin-a", ModelTier.High));
    }

    [Theory]
    [InlineData("openrouter/auto")]
    [InlineData("vendor/fixed-model")]
    public async Task Configured_snapshot_is_never_claimed_to_be_the_actual_resolved_model(string upstream)
    {
        await using var host = await RouterHost.StartAsync();
        var row = Usage();
        row.UpstreamModelId = upstream;
        row.CostSource = "unknown";
        await SeedAsync(host, row);
        using var context = Dashboard(host);
        var cut = context.Render<RequestDetails>(p => p.Add(c => c.Id, row.Id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#request-routing")), Wait);

        Assert.Equal(upstream, Metadata(cut, "request-routing")["Configured upstream snapshot"]);
        Assert.Equal("Unknown (not reported)", Metadata(cut, "request-routing")["Actual resolved upstream model"]);
        Assert.Equal("Unknown", Metadata(cut, "request-usage")["Cost (USD)"]);
    }

    [Fact]
    public async Task Legacy_detail_is_unknown_unassociated_and_a_missing_id_is_friendly()
    {
        await using var host = await RouterHost.StartAsync();
        var legacy = Usage(null);
        await SeedAsync(host, legacy);
        using var context = Dashboard(host);
        var cut = context.Render<RequestDetails>(p => p.Add(c => c.Id, legacy.Id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#request-result")), Wait);
        Assert.Equal("Unknown", Metadata(cut, "request-result")["Outcome"]);
        Assert.Contains("Completion outcome was not recorded", cut.Markup);
        Assert.Contains("unassociated", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("a"), a => a.GetAttribute("href")!.StartsWith("/ui/pins"));

        var missing = context.Render<RequestDetails>(p => p.Add(c => c.Id, long.MaxValue));
        missing.WaitForAssertion(() => Assert.Contains("Request not found", missing.Markup), Wait);
        Assert.Equal("/ui", missing.Find("section a").GetAttribute("href"));
    }

    [Fact]
    public async Task History_page_keeps_deleted_pin_requests_and_links_older_pages_and_details()
    {
        await using var host = await RouterHost.StartAsync();
        await SeedAsync(host, Enumerable.Range(0, 51).Select(_ => Usage()).ToArray());
        using var context = Dashboard(host);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardFormat.PinUrl("pin-a", ModelTier.High));
        var cut = context.Render<PinHistory>();
        cut.WaitForAssertion(() => Assert.Equal(50, cut.FindAll("#pin-requests tbody tr").Count), Wait);
        Assert.Contains("missing or was deleted", cut.Markup);
        Assert.Contains("Legacy requests", cut.Markup);
        Assert.StartsWith("/ui/requests/", cut.Find("#pin-requests a").GetAttribute("href"));
        var older = cut.Find("nav.pagination a").GetAttribute("href")!;
        Assert.Contains("beforeId=", older);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(older);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#pin-requests tbody tr")), Wait);
        Assert.Equal("Newest requests", cut.Find("nav.pagination a").TextContent);
    }

    [Theory]
    [InlineData("/ui/pins?sessionKey=pin-a&tier=high")]
    [InlineData("/ui/requests/1")]
    public async Task History_and_details_inherit_dashboard_host_and_origin_guards(string path)
    {
        await using var host = await RouterHost.StartAsync();
        using var normal = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
        using var foreignHost = new HttpRequestMessage(HttpMethod.Get, path);
        foreignHost.Headers.Host = "evil.example";
        using var hostResponse = await host.Client.SendAsync(foreignHost);
        Assert.Equal(HttpStatusCode.BadRequest, hostResponse.StatusCode);
        using var foreignOrigin = new HttpRequestMessage(HttpMethod.Get, path);
        foreignOrigin.Headers.Add("Origin", "https://evil.example");
        using var originResponse = await host.Client.SendAsync(foreignOrigin);
        Assert.Equal(HttpStatusCode.BadRequest, originResponse.StatusCode);
    }
}
