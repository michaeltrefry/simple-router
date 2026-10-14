using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.ResponsesFixtures;

namespace SimpleRouter.Tests;

/// <summary><c>GET /v1/subscriptions/usage</c> and <c>GET /v1/sessions/{id}/cost</c>, read as the orchestrator reads them.</summary>
public class DarkFactoryEndpointsTests
{
    private const string Seed = """
        {
          "defaultTier": "high",
          "models": [
            { "id": "claude-a", "provider": "claude", "upstreamModelId": "upstream-a", "enabled": true },
            { "id": "claude-b", "provider": "claude", "upstreamModelId": "upstream-b", "enabled": true },
            { "id": "claude-off", "provider": "claude", "upstreamModelId": "upstream-off", "enabled": false },
            { "id": "claude-untiered", "provider": "claude", "upstreamModelId": "upstream-u", "enabled": true },
            { "id": "codex-c", "provider": "codex", "upstreamModelId": "upstream-c", "enabled": true },
            { "id": "local-l", "provider": "local", "upstreamModelId": "upstream-l", "enabled": true, "baseUrl": "http://local-l.test/v1", "contextWindow": 100000 }
          ],
          "tiers": {
            "high": [ "claude-a", "claude-off" ],
            "mid": [ "claude-b", "local-l" ],
            "low": [ "codex-c" ]
          },
          "aliases": [],
          "prices": {
            "claude-a": { "input": 3, "output": 15, "cacheRead": 0.3, "cacheWrite": 3.75 },
            "codex-c": { "input": 1, "output": 8, "cacheRead": 0.1, "cacheWrite": 1 },
            "local-l": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0 }
          }
        }
        """;

    private static async Task<RouterHost> StartAsync()
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, Seed);
        var local = new FakeHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/models")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : Json("""{"id":"c","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"hi"}}],"usage":{"prompt_tokens":50,"completion_tokens":7}}"""),
        };
        var host = await RouterHost.StartInAsync(RouterHost.NewDataDirectory(), local, $"--SimpleRouter:SeedFile={seedPath}");
        await host.AuthorizedClientAsync();
        return host;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpRequestMessage Message(string sessionId, string modelClass, bool stream = false, string path = "/v1/messages")
    {
        var body = new JsonObject
        {
            ["model"] = "default",
            ["max_tokens"] = 64,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hello" } },
            ["output_config"] = new JsonObject { ["effort"] = "high" },
        };
        if (stream)
        {
            body["stream"] = true;
        }
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionKey.HeaderName, sessionId);
        request.Headers.Add(TierResolver.HeaderName, modelClass);
        return request;
    }

    private static async Task CoolAsync(RouterHost host, string modelId, DateTimeOffset until, FaultKind reason = FaultKind.Limited)
    {
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        db.ModelCooldowns.Add(new ModelCooldown { ModelId = modelId, Until = until, Reason = reason });
        await db.SaveChangesAsync();
    }

    private static async Task<SubscriptionUsage> UsageAsync(RouterHost host)
    {
        using var response = await host.Client.GetAsync("/v1/subscriptions/usage");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubscriptionUsage>())!;
    }

    private static async Task<SessionCost?> CostAsync(RouterHost host, string sessionId)
    {
        using var response = await host.Client.GetAsync($"/v1/sessions/{Uri.EscapeDataString(sessionId)}/cost");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<SessionCost>();
    }

    [Fact]
    public async Task Without_a_subscription_login_usage_knows_no_credential_and_is_not_exhausted()
    {
        await using var host = await StartAsync();

        var usage = await UsageAsync(host);

        Assert.False(usage.AllExhausted);
        Assert.Equal(0, usage.KnownCredentials);
        Assert.Null(usage.ResumesAt);
        Assert.Empty(usage.Credentials!);
        Assert.InRange(usage.AsOf, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task With_routable_subscriptions_usage_lists_both_managed_logins_as_enrolled_and_not_exhausted()
    {
        await using var host = await StartAsync();
        await host.LoginAsync();
        await host.LoginCodexAsync();
        await CoolAsync(host, "claude-a", DateTimeOffset.UtcNow.AddHours(1));

        var usage = await UsageAsync(host);

        Assert.False(usage.AllExhausted);
        Assert.Null(usage.ResumesAt);
        Assert.Equal(2, usage.KnownCredentials);
        Assert.Equal(2, usage.EnrolledCredentials);
        Assert.Equal(
            [new UsageCredential("claude", "managed", true, true, "ok"), new UsageCredential("codex", "managed", true, true, "ok")],
            usage.Credentials!.OrderBy(c => c.Provider));
    }

    [Fact]
    public async Task When_every_subscription_model_is_cooling_or_rejected_usage_is_exhausted_until_the_earliest_reset_whatever_the_locals()
    {
        await using var host = await StartAsync();
        await host.LoginAsync();
        await host.LoginCodexAsync();
        var now = DateTimeOffset.UtcNow;
        var earliest = new DateTimeOffset(now.AddMinutes(20).UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        await CoolAsync(host, "claude-a", now.AddHours(5));
        await CoolAsync(host, "claude-b", earliest.AddHours(1));
        await CoolAsync(host, "codex-c", earliest, FaultKind.AuthRejected);
        // The local model stays healthy and eligible, and the untiered/disabled Claude models are never routable: none counts.

        var usage = await UsageAsync(host);

        Assert.True(usage.AllExhausted);
        Assert.Equal(earliest, usage.ResumesAt);
        Assert.Equal(2, usage.KnownCredentials);
        Assert.Equal(
            [new UsageCredential("claude", "managed", true, false, "cooling"), new UsageCredential("codex", "managed", true, false, "auth_rejected")],
            usage.Credentials!.OrderBy(c => c.Provider));
        Assert.Equal(0, usage.EnrolledCredentials);
    }

    [Fact]
    public async Task One_routable_subscription_model_keeps_usage_from_being_exhausted()
    {
        await using var host = await StartAsync();
        await host.LoginAsync();
        await host.LoginCodexAsync();
        await CoolAsync(host, "claude-a", DateTimeOffset.UtcNow.AddHours(1));
        await CoolAsync(host, "codex-c", DateTimeOffset.UtcNow.AddHours(1));

        var usage = await UsageAsync(host);

        Assert.False(usage.AllExhausted);
        Assert.Null(usage.ResumesAt);
        Assert.Equal(new UsageCredential("codex", "managed", true, false, "cooling"), usage.Credentials!.Single(c => c.Provider == "codex"));
        Assert.True(usage.Credentials!.Single(c => c.Provider == "claude").Routable);
    }

    [Fact]
    public async Task Session_cost_sums_the_priced_usage_of_every_served_request_of_the_client_session()
    {
        await using var host = await StartAsync();
        await host.LoginAsync();
        await host.LoginCodexAsync();
        var streamed = true;
        host.Upstream.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/count_tokens")
            ? Json("""{"input_tokens":12}""")
            : streamed
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"upstream-a\",\"content\":[],\"usage\":{\"input_tokens\":1000,\"cache_read_input_tokens\":2000,\"cache_creation_input_tokens\":500,\"output_tokens\":1}}}\n\n"
                        + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"the usage word in text\"}}\n\n"
                        + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                        + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":300}}\n\n"
                        + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
                        Encoding.UTF8, "text/event-stream"),
                }
                : Json("""{"id":"m","type":"message","role":"assistant","content":[],"usage":{"input_tokens":10,"output_tokens":20}}""");
        host.Codex.Respond = _ => Stream(ReasoningTextAndToolCall);

        using (var r = await host.Client.SendAsync(Message("df-1", "high", stream: true)))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("message_stop", await r.Content.ReadAsStringAsync());
        }
        streamed = false;
        (await host.Client.SendAsync(Message("df-1", "high"))).Dispose();
        (await host.Client.SendAsync(Message("df-1", "high", path: "/v1/messages/count_tokens"))).Dispose();
        (await host.Client.SendAsync(Message("df-1", "low", stream: true))).Dispose();
        (await host.Client.SendAsync(Message("df-1", "mid"))).Dispose();
        (await host.Client.SendAsync(Message("df-other", "high"))).Dispose();

        var cost = await CostAsync(host, "df-1");

        // Claude stream: 1000×3 + 300×15 + 2000×0.3 + 500×3.75 = 9975; Claude JSON: 10×3 + 20×15 = 330;
        // Codex (translated, 120 input of which 100 cached): 20×1 + 45×8 + 100×0.1 = 390; local: $0.
        Assert.Equal(new SessionCost("df-1", 4, 9975 + 330 + 390), cost);
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        var rows = await db.RequestUsages.Where(u => u.ClientSessionId == "df-1").OrderBy(u => u.Id).ToListAsync();
        Assert.Equal(["claude-a", "claude-a", "codex-c", "local-l"], rows.Select(r => r.ModelId));
        Assert.Equal((1000, 300, 2000, 500), (rows[0].InputTokens, rows[0].OutputTokens, rows[0].CacheReadTokens, rows[0].CacheWriteTokens));
        Assert.Equal((20, 45, 100, 0), (rows[2].InputTokens, rows[2].OutputTokens, rows[2].CacheReadTokens, rows[2].CacheWriteTokens));
        Assert.Equal((50, 7, 0L), (rows[3].InputTokens, rows[3].OutputTokens, rows[3].CostUsdMicros));
        Assert.All(rows, r => Assert.Equal((200, "high"), (r.Status, r.Effort)));
        Assert.Equal([ModelTier.High, ModelTier.High, ModelTier.Low, ModelTier.Mid], rows.Select(r => r.Tier));
    }

    [Fact]
    public async Task A_stream_the_client_abandons_midway_is_still_recorded_with_the_usage_it_reported()
    {
        await using var host = await StartAsync();
        await host.LoginAsync();
        var upstream = new System.IO.Pipelines.Pipe();
        await upstream.Writer.WriteAsync(Encoding.UTF8.GetBytes(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":1000,\"output_tokens\":1}}}\n\n"));
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(upstream.Reader.AsStream()) { Headers = { { "Content-Type", "text/event-stream" } } },
        };

        using (var response = await host.Client.SendAsync(Message("df-cut", "high", stream: true), HttpCompletionOption.ResponseHeadersRead))
        {
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            Assert.StartsWith("event: message_start", await reader.ReadLineAsync());
        }

        SessionCost? cost = null;
        await WaitUntilAsync(async () => (cost = await CostAsync(host, "df-cut")) is not null);
        Assert.Equal(new SessionCost("df-cut", 1, 1000 * 3 + 1 * 15), cost);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Session_cost_is_404_for_a_session_with_no_recorded_request()
    {
        await using var host = await StartAsync();

        using var response = await host.Client.GetAsync("/v1/sessions/never-seen/cost");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/v1/subscriptions/usage")]
    [InlineData("/v1/sessions/df-1/cost")]
    public async Task Both_endpoints_require_the_router_key(string path)
    {
        await using var host = await StartAsync();
        using var anonymous = new HttpClient { BaseAddress = host.Client.BaseAddress };

        using var response = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
