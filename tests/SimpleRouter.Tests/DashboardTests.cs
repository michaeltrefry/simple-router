using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Dashboard;
using SimpleRouter.Dashboard.Components.Panels;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

/// <summary>The monitoring dashboard: panels over seeded state, live updates from served requests and cooldowns, and its HTTP surface.</summary>
public class DashboardTests
{
    private const string Seed = """
        {
          "defaultTier": "high",
          "models": [
            { "id": "claude-a", "provider": "claude", "upstreamModelId": "upstream-a", "enabled": true },
            { "id": "claude-x", "provider": "claude", "upstreamModelId": "upstream-x", "enabled": true },
            { "id": "codex-c", "provider": "codex", "upstreamModelId": "upstream-c", "enabled": true }
          ],
          "tiers": {
            "high": [ "claude-a" ],
            "max": [ "claude-x" ],
            "low": [ "codex-c" ]
          },
          "aliases": []
        }
        """;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<RouterHost> StartAsync()
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, Seed);
        var host = await RouterHost.StartInAsync(RouterHost.NewDataDirectory(), $"--SimpleRouter:SeedFile={seedPath}");
        await host.LoginAsync(accessToken: "secret-access-token", refreshToken: "secret-refresh-token");
        host.Upstream.Respond = _ => Json("""{"id":"msg","type":"message","usage":{"input_tokens":12,"output_tokens":34}}""");
        await host.AuthorizedClientAsync();
        return host;
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

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpRequestMessage Message(string sessionId, string modelClass)
    {
        var body = new JsonObject
        {
            ["model"] = "default",
            ["max_tokens"] = 64,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hello" } },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionKey.HeaderName, sessionId);
        request.Headers.Add(TierResolver.HeaderName, modelClass);
        return request;
    }

    private static async Task SeedAsync(RouterHost host, Action<RouterDbContext> seed)
    {
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static string Cells(IRenderedComponent<RecentRequests> cut, int row) =>
        string.Join("|", cut.FindAll("#recent-requests tbody tr")[row].QuerySelectorAll("td").Select(td => td.TextContent.Trim()));

    [Fact]
    public async Task The_recent_requests_view_lists_session_tier_model_effort_latency_and_status_newest_first()
    {
        await using var host = await StartAsync();
        var at = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        await SeedAsync(host, db => db.RequestUsages.AddRange(
            new RequestUsage { At = at, ClientSessionId = "session-old", ModelId = "claude-a", Provider = ProviderKind.Claude, Tier = ModelTier.High, Status = 200, LatencyMs = 850, Effort = "low", InputTokens = 1200, OutputTokens = 30, CostUsdMicros = 12_500 },
            new RequestUsage { At = at.AddMinutes(1), ClientSessionId = "session-new", ModelId = "codex-c", Provider = ProviderKind.Codex, Tier = ModelTier.Low, Status = 429, LatencyMs = 2340, Effort = "high" }));
        using var dashboard = Dashboard(host);

        var cut = dashboard.Render<RecentRequests>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#recent-requests tbody tr").Count), Wait);
        Assert.Equal("2026-10-14 12:01:00Z|session-new|low|codex-c|high|2.3 s|429|0 / 0|$0.0000", Cells(cut, 0));
        Assert.Equal("2026-10-14 12:00:00Z|session-old|high|claude-a|low|850 ms|200|1,200 / 30|$0.0125", Cells(cut, 1));
        Assert.Equal("pill warn", cut.FindAll("#recent-requests tbody tr")[0].QuerySelector(".pill")!.ClassName);
    }

    [Fact]
    public async Task The_pins_and_model_views_show_a_seeded_cooldown_with_its_reset_time()
    {
        await using var host = await StartAsync();
        var until = DateTimeOffset.UtcNow.AddHours(3);
        await SeedAsync(host, db =>
        {
            db.SessionPins.Add(new SessionPin { SessionKey = "0123456789abcdef0123", Tier = ModelTier.Max, ModelId = "claude-x", ClientSessionId = "session-1", CreatedAt = DateTimeOffset.UtcNow });
            db.ModelCooldowns.Add(new ModelCooldown { ModelId = "claude-x", Until = until, Reason = FaultKind.Limited });
        });
        using var dashboard = Dashboard(host);

        var pins = dashboard.Render<SessionPins>();
        var models = dashboard.Render<ModelStatusTable>();

        var expected = $"cooling until {until.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)}";
        pins.WaitForAssertion(() => Assert.Single(pins.FindAll("#session-pins tbody tr")), Wait);
        var pin = pins.Find("#session-pins tbody tr").QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ToList();
        Assert.Equal(["session-1", "0123456789ab", "max", "claude-x", expected], pin[..5]);
        models.WaitForAssertion(() => Assert.Equal(3, models.FindAll("#model-statuses tbody tr").Count), Wait);
        var statuses = models.FindAll("#model-statuses tbody tr")
            .ToDictionary(r => r.QuerySelector("td")!.TextContent.Trim(), r => r.QuerySelector(".pill")!.TextContent.Trim());
        Assert.Equal(expected, statuses["claude-x"]);
        Assert.Equal("eligible", statuses["claude-a"]);
        Assert.Equal("not logged in", statuses["codex-c"]);
    }

    [Fact]
    public async Task The_accounts_view_shows_login_state_and_the_model_list_fetch_without_any_secret()
    {
        await using var host = await StartAsync();
        var fetched = new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);
        await SeedAsync(host, db => db.ProviderModelLists.Add(new ProviderModelList
        {
            Provider = ProviderKind.Claude, ModelIds = ["upstream-a", "upstream-x"], FetchedAt = fetched, CheckedAt = fetched.AddHours(6),
            LastError = "Claude returned HTTP 503 for its model list.",
        }));
        using var dashboard = Dashboard(host);

        var cut = dashboard.Render<SubscriptionAccounts>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#subscription-accounts tbody tr").Count), Wait);
        var rows = cut.FindAll("#subscription-accounts tbody tr")
            .Select(r => string.Join("|", r.QuerySelectorAll("td").Select(td => td.TextContent.Trim()))).ToList();
        Assert.Equal("claude|ok|2026-10-14 09:30:00Z|2|Claude returned HTTP 503 for its model list.", rows[0]);
        Assert.Equal("codex|not logged in|—|—|—", rows[1]);
        Assert.DoesNotContain("secret-", cut.Markup);
    }

    [Fact]
    public async Task A_request_served_through_the_router_appears_in_the_rendered_recent_requests_without_a_reload()
    {
        await using var host = await StartAsync();
        using var dashboard = Dashboard(host);
        var cut = dashboard.Render<RecentRequests>();
        cut.WaitForAssertion(() => Assert.Contains("No requests served yet.", cut.Markup), Wait);

        using var response = await host.Client.SendAsync(Message("live-session", "high"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#recent-requests tbody tr")), Wait);
        var cells = Cells(cut, 0).Split('|');
        Assert.Equal(["live-session", "high", "claude-a"], cells[1..4]);
        Assert.Equal("200", cells[6]);
        Assert.Equal("12 / 34", cells[7]);
    }

    [Fact]
    public async Task An_upstream_429_shows_the_cooldown_and_its_reset_in_the_rendered_pins_and_model_views()
    {
        await using var host = await StartAsync();
        var reset = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds());
        host.Upstream.Respond = _ =>
        {
            var response = Json("""{"type":"error","error":{"type":"rate_limit_error","message":"limit"}}""", HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-status", "rejected");
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return response;
        };
        using var dashboard = Dashboard(host);
        var pins = dashboard.Render<SessionPins>();
        var models = dashboard.Render<ModelStatusTable>();
        models.WaitForAssertion(() => Assert.Equal(3, models.FindAll("#model-statuses tbody tr").Count), Wait);

        using var response = await host.Client.SendAsync(Message("limited-session", "max"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var expected = $"cooling until {reset.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)}";
        pins.WaitForAssertion(() =>
        {
            var row = pins.Find("#session-pins tbody tr");
            Assert.Contains("limited-session", row.TextContent);
            Assert.Equal(expected, row.QuerySelector(".pill")!.TextContent.Trim());
        }, Wait);
        models.WaitForAssertion(() => Assert.Contains(models.FindAll("#model-statuses tbody tr"),
            r => r.TextContent.Contains("claude-x") && r.QuerySelector(".pill")!.TextContent.Trim() == expected), Wait);
    }

    [Fact]
    public async Task The_dashboard_is_served_without_the_gateway_key_while_v1_still_requires_it()
    {
        await using var host = await StartAsync();
        using var anonymous = new HttpClient { BaseAddress = host.Client.BaseAddress };

        using var page = await anonymous.GetAsync("/ui");
        using var models = await anonymous.GetAsync("/v1/models");
        using var messages = await anonymous.SendAsync(Message("anonymous", "high"));

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Recent requests", html);
        Assert.Contains("_framework/blazor.web.js", html);
        Assert.DoesNotContain("secret-", html);
        Assert.Equal(HttpStatusCode.Unauthorized, models.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, messages.StatusCode);
        Assert.Empty(host.Upstream.Requests);
    }
}
