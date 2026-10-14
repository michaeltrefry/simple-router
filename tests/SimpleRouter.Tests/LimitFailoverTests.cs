using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Data;
using SimpleRouter.Local;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.ResponsesFixtures;

namespace SimpleRouter.Tests;

/// <summary>Usage limits, plan rejections and unreachable models: cooldowns, in-request failover and re-pinning, exhaustion.</summary>
public class LimitFailoverTests
{
    private const string Seed = """
        {
          "defaultTier": "high",
          "models": [
            { "id": "claude-a", "provider": "claude", "upstreamModelId": "upstream-a", "enabled": true },
            { "id": "claude-b", "provider": "claude", "upstreamModelId": "upstream-b", "enabled": true },
            { "id": "claude-x", "provider": "claude", "upstreamModelId": "upstream-x", "enabled": true },
            { "id": "codex-c", "provider": "codex", "upstreamModelId": "upstream-c", "enabled": true },
            { "id": "local-l", "provider": "local", "upstreamModelId": "upstream-l", "enabled": true, "baseUrl": "http://local-l.test/v1", "contextWindow": 1000 }
          ],
          "tiers": {
            "high": [ "claude-a", "claude-b" ],
            "max": [ "claude-x" ],
            "mid": [ "claude-a", "local-l" ],
            "low": [ "codex-c", "claude-b" ]
          },
          "aliases": []
        }
        """;

    /// <summary>Per-upstream-model Claude answers; anything unset answers 200.</summary>
    private sealed class Fakes
    {
        public Dictionary<string, Func<HttpResponseMessage>> Claude { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> LocalChat { get; set; } = _ => Json("""{"id":"c","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"local"}}]}""");
    }

    private static async Task<(RouterHost Host, Fakes Fakes)> StartAsync(string? dataDirectory = null)
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, Seed);
        var fakes = new Fakes();
        var local = new FakeHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/models") ? new HttpResponseMessage(HttpStatusCode.OK) : fakes.LocalChat(request),
        };
        var host = await RouterHost.StartInAsync(dataDirectory ?? RouterHost.NewDataDirectory(), local, $"--SimpleRouter:SeedFile={seedPath}");
        await host.LoginAsync();
        await host.LoginCodexAsync();
        host.Upstream.Respond = request =>
        {
            var model = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!["model"]!.GetValue<string>();
            return fakes.Claude.TryGetValue(model, out var respond) ? respond() : Json($$"""{"id":"msg","type":"message","model":"{{model}}"}""");
        };
        host.Codex.Respond = _ => Stream(ReasoningTextAndToolCall);
        await host.AuthorizedClientAsync();
        return (host, fakes);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Limited(params (string Name, string Value)[] headers)
    {
        var response = Json("""{"type":"error","error":{"type":"rate_limit_error","message":"limit"}}""", HttpStatusCode.TooManyRequests);
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    /// <param name="laterTurnChars">Grows the conversation after its first user message, which (with the session id) keys the session.</param>
    private static HttpRequestMessage Message(string sessionId, string modelClass, bool stream = false, int laterTurnChars = 0)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hello" } };
        if (laterTurnChars > 0)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "ok" });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = new string('x', laterTurnChars) });
        }
        var body = new JsonObject
        {
            ["model"] = "default",
            ["max_tokens"] = 64,
            ["messages"] = messages,
        };
        if (stream)
        {
            body["stream"] = true;
        }
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionKey.HeaderName, sessionId);
        request.Headers.Add(TierResolver.HeaderName, modelClass);
        return request;
    }

    private static List<string> ClaudeModels(RouterHost host) =>
        host.Upstream.Requests.Select(r => JsonNode.Parse(r.Body)!["model"]!.GetValue<string>()).ToList();

    private static List<FakeHandler.Recorded> LocalChats(RouterHost host)
    {
        lock (host.Local.Requests)
        {
            return host.Local.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/chat/completions")).ToList();
        }
    }

    private static async Task<string> PinAsync(RouterHost host, string sessionId)
    {
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        return (await db.SessionPins.SingleAsync(p => p.ClientSessionId == sessionId)).ModelId;
    }

    private static async Task<ModelStatusEntry> StatusAsync(RouterHost host, string modelId) =>
        (await host.Services.GetRequiredService<ModelSelector>().StatusesAsync(CancellationToken.None)).Single(s => s.ModelId == modelId);

    private static async Task<DateTimeOffset> CoolingUntilAsync(RouterHost host, string modelId)
    {
        var status = await StatusAsync(host, modelId);
        Assert.Equal(ModelStatus.Cooling, status.Status);
        Assert.Equal($"cooling until {status.CoolingUntil!.Value.UtcDateTime:u}", status.Label);
        return status.CoolingUntil.Value;
    }

    private static void AssertNear(DateTimeOffset expected, DateTimeOffset actual) =>
        Assert.InRange(actual, expected.AddSeconds(-5), expected.AddSeconds(5));

    private static async Task SendOkAsync(RouterHost host, HttpRequestMessage request)
    {
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static (string, string)[] LimitHeadersFor(string kind) => kind switch
    {
        "unified status, unix reset" => [("anthropic-ratelimit-unified-status", "rejected"),
            ("anthropic-ratelimit-unified-reset", DateTimeOffset.UtcNow.AddSeconds(7200).ToUnixTimeSeconds().ToString())],
        "5h utilization, RFC 3339 reset" => [("anthropic-ratelimit-unified-5h-utilization", "1.0"),
            ("anthropic-ratelimit-unified-5h-reset", DateTimeOffset.UtcNow.AddSeconds(10800).ToString("yyyy-MM-ddTHH:mm:ssZ"))],
        "7d status, retry-after" => [("anthropic-ratelimit-unified-7d-status", "rejected"), ("retry-after", "30")],
        "burst retry-after only" => [("retry-after", "120")],
        _ => [],
    };

    [Theory]
    [InlineData("unified status, unix reset", 7200)]
    [InlineData("5h utilization, RFC 3339 reset", 10800)]
    [InlineData("7d status, retry-after", 30)]
    [InlineData("burst retry-after only", 120)]
    [InlineData("no reset at all", 60)]
    public async Task A_429_is_served_by_the_next_model_in_the_tier_which_keeps_the_session_while_the_first_cools_until_its_reset(
        string kind, int coolSeconds)
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => Limited(LimitHeadersFor(kind));

        var before = DateTimeOffset.UtcNow;
        await SendOkAsync(host, Message("s1", "high"));

        Assert.Equal(["upstream-a", "upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        AssertNear(before.AddSeconds(coolSeconds), await CoolingUntilAsync(host, "claude-a"));

        await SendOkAsync(host, Message("s1", "high"));
        await SendOkAsync(host, Message("s2", "high"));
        Assert.Equal(["upstream-a", "upstream-b", "upstream-b", "upstream-b"], ClaudeModels(host));
    }

    [Fact]
    public async Task A_successful_response_whose_headers_report_an_exhausted_window_cools_the_model_for_later_requests()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        var reset = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds();
        fakes.Claude["upstream-a"] = () =>
        {
            var ok = Json("""{"id":"msg","type":"message"}""");
            ok.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-status", "rejected");
            ok.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-reset", reset.ToString());
            return ok;
        };

        await SendOkAsync(host, Message("s1", "high"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset), await CoolingUntilAsync(host, "claude-a"));
        await SendOkAsync(host, Message("s1", "high"));

        Assert.Equal(["upstream-a", "upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
    }

    [Fact]
    public async Task An_overloaded_model_is_skipped_for_a_short_cooldown_and_the_request_served_by_the_next()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => Json("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", (HttpStatusCode)529);

        var before = DateTimeOffset.UtcNow;
        await SendOkAsync(host, Message("s1", "high"));

        Assert.Equal(["upstream-a", "upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        AssertNear(before.AddSeconds(30), await CoolingUntilAsync(host, "claude-a"));
    }

    [Fact]
    public async Task A_codex_plan_rejection_re_pins_to_the_next_model_and_cools_codex_for_hours()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        host.Codex.Respond = _ => Json("""{"error":{"code":"usage_not_included","message":"Your plan does not include Codex."}}""", HttpStatusCode.Forbidden);

        var before = DateTimeOffset.UtcNow;
        await SendOkAsync(host, Message("s1", "low", stream: true));

        Assert.Single(host.Codex.Requests);
        Assert.Equal(["upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        AssertNear(before.AddHours(6), await CoolingUntilAsync(host, "codex-c"));
    }

    [Fact]
    public async Task A_codex_429_cools_codex_until_the_reset_its_limit_headers_report()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        host.Codex.Respond = _ =>
        {
            var limited = Json("""{"error":{"type":"usage_limit_reached","message":"The usage limit has been reached."}}""", HttpStatusCode.TooManyRequests);
            limited.Headers.TryAddWithoutValidation("x-codex-primary-used-percent", "100");
            limited.Headers.TryAddWithoutValidation("x-codex-primary-reset-after-seconds", "900");
            limited.Headers.TryAddWithoutValidation("x-codex-secondary-used-percent", "40");
            return limited;
        };

        var before = DateTimeOffset.UtcNow;
        await SendOkAsync(host, Message("s1", "low"));

        Assert.Equal(["upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        AssertNear(before.AddSeconds(900), await CoolingUntilAsync(host, "codex-c"));
    }

    [Fact]
    public async Task A_codex_429_without_limit_headers_uses_the_reset_in_its_error_body()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var reset = DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeSeconds();
        host.Codex.Respond = _ => Json($$$"""{"error":{"type":"usage_limit_reached","message":"limit","resets_at":{{{reset}}}}}""", HttpStatusCode.TooManyRequests);

        await SendOkAsync(host, Message("s1", "low"));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset), await CoolingUntilAsync(host, "codex-c"));
    }

    [Fact]
    public async Task A_successful_codex_response_reporting_a_spent_window_cools_codex_for_the_next_request()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var reset = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        host.Codex.Respond = _ =>
        {
            var ok = Stream(ReasoningTextAndToolCall);
            ok.Headers.TryAddWithoutValidation("x-codex-secondary-used-percent", "100.0");
            ok.Headers.TryAddWithoutValidation("x-codex-secondary-reset-at", reset.ToString());
            return ok;
        };

        await SendOkAsync(host, Message("s1", "low", stream: true));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset), await CoolingUntilAsync(host, "codex-c"));
        await SendOkAsync(host, Message("s1", "low", stream: true));

        Assert.Single(host.Codex.Requests);
        Assert.Equal(["upstream-b"], ClaudeModels(host));
    }

    [Fact]
    public async Task A_limit_after_the_first_byte_is_not_failed_over_but_still_cools_the_model()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        host.Codex.Respond = _ => Stream(Sse(Created, TextDelta(0, "Half"),
            """{"type":"response.failed","response":{"error":{"code":"usage_limit_reached","message":"Usage limit reached."}}}"""));

        using var response = await host.Client.SendAsync(Message("s1", "low", stream: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = AnthropicSse.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Usage limit reached.", message.Error);
        Assert.Equal("Half", Assert.Single(message.Blocks).Text);
        Assert.Single(host.Codex.Requests);
        Assert.Empty(host.Upstream.Requests);
        await CoolingUntilAsync(host, "codex-c");
    }

    [Fact]
    public async Task A_refused_local_connection_is_served_by_the_next_model_and_re_pins_the_session()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.LocalChat = _ => throw new HttpRequestException("connection refused");

        await SendOkAsync(host, Message("s1", "mid"));

        Assert.Single(LocalChats(host));
        Assert.Equal(["upstream-a"], ClaudeModels(host));
        Assert.Equal("claude-a", await PinAsync(host, "s1"));
        Assert.False(host.Services.GetRequiredService<LocalHealth>().IsHealthy("local-l"));
        Assert.Equal(ModelStatus.Unhealthy, (await StatusAsync(host, "local-l")).Status);
    }

    [Fact]
    public async Task A_request_too_large_for_the_pinned_local_is_served_by_a_fitting_model_which_keeps_the_session()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        await SendOkAsync(host, Message("s1", "mid"));
        Assert.Equal("local-l", await PinAsync(host, "s1"));

        await SendOkAsync(host, Message("s1", "mid", laterTurnChars: 8000));
        await SendOkAsync(host, Message("s1", "mid"));

        Assert.Single(LocalChats(host));
        Assert.Equal(["upstream-a", "upstream-a"], ClaudeModels(host));
        Assert.Equal("claude-a", await PinAsync(host, "s1"));
    }

    [Fact]
    public async Task A_local_context_length_rejection_is_served_by_a_fitting_model_and_re_pins_the_session()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.LocalChat = _ => Json("""{"error":{"message":"This model's maximum context length is 1000 tokens.","type":"invalid_request_error"}}""", HttpStatusCode.BadRequest);

        await SendOkAsync(host, Message("s1", "mid"));

        Assert.Single(LocalChats(host));
        Assert.Equal(["upstream-a"], ClaudeModels(host));
        Assert.Equal("claude-a", await PinAsync(host, "s1"));
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "local-l")).Status);
    }

    [Fact]
    public async Task When_every_model_in_the_tier_is_limited_the_answer_is_the_exhaustion_429_with_the_earliest_reset()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => Limited(("anthropic-ratelimit-unified-status", "rejected"),
            ("anthropic-ratelimit-unified-reset", DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds().ToString()));
        fakes.Claude["upstream-b"] = () => Limited(("retry-after", "600"));

        using var response = await host.Client.SendAsync(Message("s1", "high"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("high", response.Headers.GetValues(MessageRouter.ModelClassHeader).Single());
        Assert.InRange(int.Parse(response.Headers.GetValues("Retry-After").Single()), 595, 600);
        var body = await response.Content.ReadAsStringAsync();
        var error = JsonNode.Parse(body)!;
        Assert.Equal("error", error["type"]!.GetValue<string>());
        Assert.Equal("rate_limit_error", error["error"]!["type"]!.GetValue<string>());
        var message = error["error"]!["message"]!.GetValue<string>();
        Assert.StartsWith("model_class_unavailable: ", message);
        Assert.True(DarkFactoryMatchers.ReviewerUsageLimited((int)response.StatusCode, body));
        Assert.True(DarkFactoryMatchers.WorkerUsageLimited(body));
        Assert.True(DarkFactoryMatchers.WorkerModelClassUnavailable(message));
        // Each tier member is tried once and no other tier is used.
        Assert.Equal(["upstream-a", "upstream-b"], ClaudeModels(host));
    }

    [Fact]
    public async Task With_every_model_already_cooling_the_exhaustion_429_is_answered_without_an_upstream_call_even_after_a_restart()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var (first, fakes) = await StartAsync(dataDirectory);
        fakes.Claude["upstream-a"] = () => Limited(("retry-after", "3600"));
        fakes.Claude["upstream-b"] = () => Limited(("retry-after", "1800"));
        (await first.Client.SendAsync(Message("s1", "high"))).Dispose();
        first.KeepData = true;
        await first.DisposeAsync();

        var (host, _) = await StartAsync(dataDirectory);
        await using var _host = host;
        using var response = await host.Client.SendAsync(Message("s2", "high"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.InRange(int.Parse(response.Headers.GetValues("Retry-After").Single()), 1790, 1800);
        Assert.Contains("model_class_unavailable", await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Upstream.Requests);
        await CoolingUntilAsync(host, "claude-a");
    }

    [Fact]
    public async Task A_cooled_model_is_eligible_again_once_its_reset_passes()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => Limited(("retry-after", "1"));
        await SendOkAsync(host, Message("s1", "high"));
        Assert.Equal(ModelStatus.Cooling, (await StatusAsync(host, "claude-a")).Status);

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "claude-a")).Status);
    }
}
