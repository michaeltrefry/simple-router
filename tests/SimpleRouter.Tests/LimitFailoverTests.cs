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

    private static async Task<(RouterHost Host, Fakes Fakes)> StartAsync(string? dataDirectory = null, params string[] extraArgs)
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, Seed);
        var fakes = new Fakes();
        var local = new FakeHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/models") ? new HttpResponseMessage(HttpStatusCode.OK) : fakes.LocalChat(request),
        };
        var host = await RouterHost.StartInAsync(dataDirectory ?? RouterHost.NewDataDirectory(), local, [$"--SimpleRouter:SeedFile={seedPath}", .. extraArgs]);
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

    [Theory]
    [InlineData("This model's maximum context length is 1000 tokens.")]
    [InlineData("Context overflow: the request does not fit.")]
    [InlineData("Trying to keep the first 2000 tokens when context the overflows.")]
    [InlineData("Prompt too long for this model.")]
    public async Task A_local_context_length_rejection_is_served_by_a_fitting_model_and_re_pins_the_session(string rejection)
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.LocalChat = _ => Json(new JsonObject { ["error"] = new JsonObject { ["message"] = rejection, ["type"] = "invalid_request_error" } }.ToJsonString(),
            HttpStatusCode.BadRequest);

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

    private static HttpResponseMessage ClaudeError(HttpStatusCode status, string type, string message) =>
        Json(new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = type, ["message"] = message },
        }.ToJsonString(), status);

    private static async Task<string> ExhaustionMessageAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var error = JsonNode.Parse(body)!["error"]!;
        Assert.Equal("rate_limit_error", error["type"]!.GetValue<string>());
        var message = error["message"]!.GetValue<string>();
        Assert.StartsWith("model_class_unavailable: ", message);
        Assert.True(DarkFactoryMatchers.ReviewerUsageLimited((int)response.StatusCode, body));
        Assert.True(DarkFactoryMatchers.WorkerUsageLimited(body));
        Assert.True(DarkFactoryMatchers.WorkerModelClassUnavailable(message));
        return message;
    }

    [Fact]
    public async Task A_tier_whose_only_local_refuses_the_connection_answers_the_exhaustion_429_with_the_fixed_backoff()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync())
        {
            (await db.Models.SingleAsync(m => m.Id == "claude-a")).Enabled = false;
            await db.SaveChangesAsync();
        }
        fakes.LocalChat = _ => throw new HttpRequestException("connection refused");

        using var response = await host.Client.SendAsync(Message("s1", "mid"));

        var message = await ExhaustionMessageAsync(response);
        Assert.Equal("60", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal("mid", response.Headers.GetValues(MessageRouter.ModelClassHeader).Single());
        Assert.DoesNotContain("simple-router login", message);
        Assert.Single(LocalChats(host));
        Assert.Equal(ModelStatus.Unhealthy, (await StatusAsync(host, "local-l")).Status);
    }

    [Fact]
    public async Task A_claude_403_refusing_the_model_for_the_plan_re_pins_and_cools_that_model_for_hours()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => ClaudeError(HttpStatusCode.Forbidden, "permission_error", "Your plan does not include this model.");

        var before = DateTimeOffset.UtcNow;
        await SendOkAsync(host, Message("s1", "high"));

        Assert.Equal(["upstream-a", "upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        AssertNear(before.AddHours(6), await CoolingUntilAsync(host, "claude-a"));
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "claude-b")).Status);
    }

    [Fact]
    public async Task A_claude_404_not_found_re_pins_and_cools_that_model_for_hours()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => ClaudeError(HttpStatusCode.NotFound, "not_found_error", "model: upstream-a");

        var before = DateTimeOffset.UtcNow;
        await SendOkAsync(host, Message("s1", "high"));

        Assert.Equal(["upstream-a", "upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        AssertNear(before.AddHours(6), await CoolingUntilAsync(host, "claude-a"));
    }

    [Fact]
    public async Task Any_other_claude_404_is_passed_through_unchanged()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        var error = new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "invalid_request_error", ["message"] = new string('n', 70_000) } }.ToJsonString();
        fakes.Claude["upstream-a"] = () => Json(error, HttpStatusCode.NotFound);

        using var response = await host.Client.SendAsync(Message("s1", "high"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(error, await response.Content.ReadAsStringAsync());
        Assert.Equal(["upstream-a"], ClaudeModels(host));
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "claude-a")).Status);
    }

    [Fact]
    public async Task A_claude_account_rejection_moves_the_session_to_another_provider_and_benches_every_claude_model_briefly()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        // The low tier's first turn goes to Codex; the next new session lands on Claude.
        await SendOkAsync(host, Message("codex-first", "low"));
        fakes.Claude["upstream-b"] = () => ClaudeError(HttpStatusCode.Forbidden, "permission_error", "OAuth authentication is currently not allowed for this organization.");

        var before = DateTimeOffset.UtcNow;
        await SendOkAsync(host, Message("s1", "low"));

        Assert.Equal(["upstream-b"], ClaudeModels(host));
        Assert.Equal(2, host.Codex.Requests.Count);
        Assert.Equal("codex-c", await PinAsync(host, "s1"));
        foreach (var claude in new[] { "claude-a", "claude-b", "claude-x" })
        {
            var status = await StatusAsync(host, claude);
            Assert.Equal(ModelStatus.AuthRejected, status.Status);
            Assert.Equal("login needed (auth rejected)", status.Label);
            AssertNear(before.AddMinutes(5), status.CoolingUntil!.Value);
        }
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "codex-c")).Status);
    }

    [Fact]
    public async Task A_tier_exhausted_by_an_account_rejection_answers_the_exhaustion_429_with_the_login_hint()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => Json("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", HttpStatusCode.Unauthorized);
        host.OAuth.Respond = _ => Json("""{"access_token":"access-new","refresh_token":"refresh-new","expires_in":3600}""");

        using var response = await host.Client.SendAsync(Message("s1", "high"));

        var message = await ExhaustionMessageAsync(response);
        Assert.EndsWith("Run 'simple-router login claude'.", message);
        Assert.InRange(int.Parse(response.Headers.GetValues("Retry-After").Single()), 295, 300);
        // Rejected again after the forced refresh.
        Assert.Equal(["upstream-a", "upstream-a"], ClaudeModels(host));
    }

    [Fact]
    public async Task A_refused_claude_connection_re_pins_and_cools_that_model_briefly()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => throw new HttpRequestException("connection refused");

        var before = DateTimeOffset.UtcNow;
        await SendOkAsync(host, Message("s1", "high"));

        Assert.Equal(["upstream-a", "upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        AssertNear(before.AddSeconds(30), await CoolingUntilAsync(host, "claude-a"));
    }

    [Fact]
    public async Task An_overload_before_anything_is_written_to_a_non_streaming_client_moves_the_request_and_cools_codex_briefly()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        host.Codex.Respond = _ => Stream(Sse(Created, TextDelta(0, "Half"),
            """{"type":"response.failed","response":{"error":{"code":"server_is_overloaded","message":"Overloaded."}}}"""));

        var before = DateTimeOffset.UtcNow;
        await SendOkAsync(host, Message("s1", "low"));

        Assert.Single(host.Codex.Requests);
        Assert.Equal(["upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        AssertNear(before.AddSeconds(30), await CoolingUntilAsync(host, "codex-c"));
    }

    [Fact]
    public async Task A_reported_reset_more_than_eight_days_away_cools_the_model_for_eight_days()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => Limited(("anthropic-ratelimit-unified-status", "rejected"),
            ("anthropic-ratelimit-unified-reset", DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds().ToString()));

        var before = DateTimeOffset.UtcNow;
        await SendOkAsync(host, Message("s1", "high"));

        AssertNear(before.AddDays(8), await CoolingUntilAsync(host, "claude-a"));
    }

    [Fact]
    public async Task A_tier_whose_only_member_fails_its_token_refresh_answers_the_exhaustion_429_with_the_login_hint()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        await host.LoginAsync(accessToken: "access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: "refresh-old");
        host.OAuth.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"invalid_grant"}""") };

        using var response = await host.Client.SendAsync(Message("s1", "max"));

        var message = await ExhaustionMessageAsync(response);
        Assert.EndsWith("Run 'simple-router login claude'.", message);
        Assert.InRange(int.Parse(response.Headers.GetValues("Retry-After").Single()), 295, 300);
        Assert.Empty(host.Upstream.Requests);
        Assert.Equal(ModelStatus.AuthRejected, (await StatusAsync(host, "claude-x")).Status);
    }
    [Fact]
    public async Task A_claude_prompt_too_long_rejection_moves_the_request_without_cooling_the_model()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => ClaudeError(HttpStatusCode.BadRequest, "invalid_request_error", "prompt is too long: 250000 tokens > 200000 maximum");

        await SendOkAsync(host, Message("s1", "high", stream: true));

        Assert.Equal(["upstream-a", "upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "claude-a")).Status);
    }

    [Fact]
    public async Task A_claude_400_that_is_not_about_context_is_passed_to_the_client()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => ClaudeError(HttpStatusCode.BadRequest, "invalid_request_error", "messages: field required");

        using var response = await host.Client.SendAsync(Message("s1", "high"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("messages: field required", await response.Content.ReadAsStringAsync());
        Assert.Equal(["upstream-a"], ClaudeModels(host));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_codex_context_length_rejection_moves_the_request_without_cooling_the_model(bool stream)
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        host.Codex.Respond = _ => Json("""{"error":{"code":"context_length_exceeded","message":"Your input exceeds the context window of this model."}}""", HttpStatusCode.BadRequest);

        await SendOkAsync(host, Message("s1", "low", stream));

        Assert.Single(host.Codex.Requests);
        Assert.Equal(["upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "codex-c")).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_codex_stream_failing_on_context_length_before_any_output_moves_the_request(bool stream)
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        host.Codex.Respond = _ => Stream(Sse(Created,
            """{"type":"response.failed","response":{"error":{"code":"context_length_exceeded","message":"Your input exceeds the context window of this model."}}}"""));

        await SendOkAsync(host, Message("s1", "low", stream));

        Assert.Single(host.Codex.Requests);
        Assert.Equal(["upstream-b"], ClaudeModels(host));
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "codex-c")).Status);
    }
    [Theory]
    [InlineData("usage_limit_reached", 60)]
    [InlineData("server_is_overloaded", 30)]
    public async Task A_codex_limit_or_overload_before_any_output_moves_a_streaming_request_and_cools_codex(string code, int seconds)
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        host.Codex.Respond = _ => Stream(Sse(Created,
            new JsonObject { ["type"] = "response.failed", ["response"] = new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = "Refused." } } }.ToJsonString()));

        var before = DateTimeOffset.UtcNow;
        using var response = await host.Client.SendAsync(Message("s1", "low", stream: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"id":"msg","type":"message","model":"upstream-b"}""", await response.Content.ReadAsStringAsync());
        Assert.Single(host.Codex.Requests);
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
        AssertNear(before.AddSeconds(seconds), await CoolingUntilAsync(host, "codex-c"));
    }

    private static HttpResponseMessage LocalSse(string sse) =>
        new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };

    [Fact]
    public async Task A_local_stream_that_errors_before_any_output_moves_the_request_and_cools_the_local_briefly()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.LocalChat = _ => LocalSse("data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\ndata: {\"error\":{\"message\":\"out of memory\"}}\n\n");

        var before = DateTimeOffset.UtcNow;
        using var response = await host.Client.SendAsync(Message("s1", "mid", stream: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"id":"msg","type":"message","model":"upstream-a"}""", await response.Content.ReadAsStringAsync());
        Assert.Single(LocalChats(host));
        Assert.Equal("claude-a", await PinAsync(host, "s1"));
        AssertNear(before.AddSeconds(30), await CoolingUntilAsync(host, "local-l"));
    }

    [Fact]
    public async Task A_local_stream_whose_first_chunk_is_a_context_error_moves_the_request_without_cooling_the_local()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.LocalChat = _ => LocalSse("data: {\"error\":{\"message\":\"This model's maximum context length is 1000 tokens.\"}}\n\n");

        await SendOkAsync(host, Message("s1", "mid", stream: true));

        Assert.Equal(["upstream-a"], ClaudeModels(host));
        Assert.Equal("claude-a", await PinAsync(host, "s1"));
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "local-l")).Status);
    }

    [Fact]
    public async Task A_local_error_after_its_first_output_is_not_failed_over()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.LocalChat = _ => LocalSse("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}\n\ndata: {\"error\":{\"message\":\"out of memory\"}}\n\n");

        using var response = await host.Client.SendAsync(Message("s1", "mid", stream: true));

        var message = AnthropicSse.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("out of memory", message.Error);
        Assert.Equal("partial", Assert.Single(message.Blocks).Text);
        Assert.Empty(host.Upstream.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_failing_local_server_is_cooled_briefly_and_the_request_served_by_the_next_model(HttpStatusCode status)
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.LocalChat = _ => new HttpResponseMessage(status) { Content = new StringContent("<html>oops</html>") };

        var before = DateTimeOffset.UtcNow;
        await SendOkAsync(host, Message("s1", "mid"));

        Assert.Single(LocalChats(host));
        Assert.Equal(["upstream-a"], ClaudeModels(host));
        Assert.Equal("claude-a", await PinAsync(host, "s1"));
        AssertNear(before.AddSeconds(30), await CoolingUntilAsync(host, "local-l"));
    }

    private static async Task CoolAsync(RouterHost host, string modelId)
    {
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        db.ModelCooldowns.Add(new ModelCooldown { ModelId = modelId, Until = DateTimeOffset.UtcNow.AddHours(1), Reason = FaultKind.Limited });
        await db.SaveChangesAsync();
    }

    private static async Task<string> PromptTooLongAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var error = JsonNode.Parse(body)!;
        Assert.Equal("error", error["type"]!.GetValue<string>());
        Assert.Equal("invalid_request_error", error["error"]!["type"]!.GetValue<string>());
        Assert.False(DarkFactoryMatchers.ReviewerUsageLimited((int)response.StatusCode, body));
        Assert.False(DarkFactoryMatchers.WorkerUsageLimited(body));
        return error["error"]!["message"]!.GetValue<string>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_every_model_in_the_tier_rejects_the_prompt_as_too_long_the_client_gets_the_upstream_400(bool stream)
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => ClaudeError(HttpStatusCode.BadRequest, "invalid_request_error", "prompt is too long: 250000 tokens > 200000 maximum");
        fakes.Claude["upstream-b"] = () => ClaudeError(HttpStatusCode.BadRequest, "invalid_request_error", "prompt is too long: 250000 tokens > 190000 maximum");

        using var response = await host.Client.SendAsync(Message("s1", "high", stream));

        Assert.Equal("prompt is too long: 250000 tokens > 190000 maximum", await PromptTooLongAsync(response));
        Assert.Equal("high", response.Headers.GetValues(MessageRouter.ModelClassHeader).Single());
        Assert.Equal(["upstream-a", "upstream-b"], ClaudeModels(host));
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "claude-a")).Status);
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "claude-b")).Status);
    }

    [Theory]
    [InlineData("codex error", "Your input exceeds the context window of this model.")]
    [InlineData("codex stream", "Your input exceeds the context window of this model.")]
    [InlineData("local error", "This model's maximum context length is 1000 tokens.")]
    [InlineData("local stream", "This model's maximum context length is 1000 tokens.")]
    public async Task A_context_rejection_with_the_rest_of_the_tier_cooling_is_the_400_named_as_a_prompt_too_long(string rejection, string upstreamMessage)
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        var local = rejection.StartsWith("local");
        await CoolAsync(host, local ? "claude-a" : "claude-b");
        var error = new JsonObject { ["code"] = "context_length_exceeded", ["message"] = upstreamMessage };
        host.Codex.Respond = _ => rejection == "codex error"
            ? Json(new JsonObject { ["error"] = error.DeepClone() }.ToJsonString(), HttpStatusCode.BadRequest)
            : Stream(Sse(Created, new JsonObject { ["type"] = "response.failed", ["response"] = new JsonObject { ["error"] = error.DeepClone() } }.ToJsonString()));
        fakes.LocalChat = _ => rejection == "local error"
            ? Json(new JsonObject { ["error"] = new JsonObject { ["message"] = upstreamMessage } }.ToJsonString(), HttpStatusCode.BadRequest)
            : LocalSse($"data: {new JsonObject { ["error"] = new JsonObject { ["message"] = upstreamMessage } }.ToJsonString()}\n\n");

        using var response = await host.Client.SendAsync(Message("s1", local ? "mid" : "low", stream: rejection.EndsWith("stream")));

        Assert.Equal($"prompt is too long: {upstreamMessage}", await PromptTooLongAsync(response));
        Assert.Equal(local ? 0 : 1, host.Codex.Requests.Count);
        Assert.Equal(local ? 1 : 0, LocalChats(host).Count);
        Assert.Empty(host.Upstream.Requests);
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, local ? "local-l" : "codex-c")).Status);
    }

    [Fact]
    public async Task A_context_rejection_and_a_limit_in_the_same_tier_is_still_the_exhaustion_429()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.Claude["upstream-a"] = () => ClaudeError(HttpStatusCode.BadRequest, "invalid_request_error", "prompt is too long: 250000 tokens > 200000 maximum");
        fakes.Claude["upstream-b"] = () => Limited(("retry-after", "600"));

        using var response = await host.Client.SendAsync(Message("s1", "high"));

        await ExhaustionMessageAsync(response);
        Assert.Equal(["upstream-a", "upstream-b"], ClaudeModels(host));
    }

    [Fact]
    public async Task A_local_stream_error_about_the_request_is_passed_to_the_client_without_failover_or_cooldown()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.LocalChat = _ => LocalSse("data: {\"error\":{\"message\":\"tools[0]: invalid schema\",\"type\":\"BadRequestError\",\"code\":400}}\n\n");

        using var response = await host.Client.SendAsync(Message("s1", "mid", stream: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;
        Assert.Equal("invalid_request_error", error["type"]!.GetValue<string>());
        Assert.Equal("tools[0]: invalid schema", error["message"]!.GetValue<string>());
        Assert.Empty(host.Upstream.Requests);
        Assert.Equal("local-l", await PinAsync(host, "s1"));
        Assert.Equal(ModelStatus.Eligible, (await StatusAsync(host, "local-l")).Status);
    }

    [Theory]
    [InlineData("{\"message\":\"bad template\",\"type\":\"invalid_request_error\"}")]
    [InlineData("{\"message\":\"bad template\",\"code\":422}")]
    [InlineData("{\"message\":\"bad template\",\"code\":\"400\"}")]
    public async Task Local_stream_errors_typed_or_coded_as_request_errors_are_passed_to_the_client(string error)
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.LocalChat = _ => LocalSse($"data: {{\"error\":{error}}}\n\n");

        using var response = await host.Client.SendAsync(Message("s1", "mid", stream: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Upstream.Requests);
    }

    [Fact]
    public async Task A_local_stream_server_error_with_a_code_still_moves_the_request_and_cools_the_local()
    {
        var (host, fakes) = await StartAsync();
        await using var _host = host;
        fakes.LocalChat = _ => LocalSse("data: {\"error\":{\"message\":\"engine crashed\",\"type\":\"server_error\",\"code\":500}}\n\n");

        await SendOkAsync(host, Message("s1", "mid", stream: true));

        Assert.Equal(["upstream-a"], ClaudeModels(host));
        Assert.Equal(ModelStatus.Cooling, (await StatusAsync(host, "local-l")).Status);
    }

    private const string Grace = "--SimpleRouter:Limits:StreamStartGrace=00:00:00.200";

    /// <summary>An upstream body whose first bytes are sent now and the rest only when the returned writer is completed.</summary>
    private static (HttpResponseMessage Response, System.IO.Pipelines.PipeWriter Remainder) Slow(string first)
    {
        var pipe = new System.IO.Pipelines.Pipe();
        pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(first)).AsTask().Wait();
        var content = new StreamContent(pipe.Reader.AsStream());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return (new HttpResponseMessage(HttpStatusCode.OK) { Content = content }, pipe.Writer);
    }

    private static async Task<string> FirstEventAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        return (await reader.ReadLineAsync(ct))!;
    }

    [Fact]
    public async Task A_codex_stream_silent_past_the_grace_period_commits_message_start_before_any_content()
    {
        var (host, _) = await StartAsync(null, Grace);
        await using var _host = host;
        var (upstream, rest) = Slow(Sse(Created, ItemAdded(0, "reasoning")));
        host.Codex.Respond = _ => upstream;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await host.Client.SendAsync(Message("s1", "low", stream: true), HttpCompletionOption.ResponseHeadersRead, deadline.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("event: message_start", await FirstEventAsync(response, deadline.Token));
        await rest.CompleteAsync();
    }

    [Fact]
    public async Task A_codex_error_first_stream_inside_a_short_grace_period_still_moves_the_request()
    {
        var (host, _) = await StartAsync(null, Grace);
        await using var _host = host;
        host.Codex.Respond = _ => Stream(Sse(Created,
            """{"type":"response.failed","response":{"error":{"code":"server_is_overloaded","message":"Refused."}}}"""));

        using var response = await host.Client.SendAsync(Message("s1", "low", stream: true));

        Assert.Equal("""{"id":"msg","type":"message","model":"upstream-b"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("claude-b", await PinAsync(host, "s1"));
    }

    [Fact]
    public async Task A_local_stream_silent_past_the_grace_period_commits_message_start_before_any_output()
    {
        var (host, fakes) = await StartAsync(null, Grace);
        await using var _host = host;
        var (upstream, rest) = Slow("data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n");
        fakes.LocalChat = _ => upstream;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await host.Client.SendAsync(Message("s1", "mid", stream: true), HttpCompletionOption.ResponseHeadersRead, deadline.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("event: message_start", await FirstEventAsync(response, deadline.Token));
        await rest.CompleteAsync();
    }

    [Fact]
    public async Task A_local_error_first_stream_inside_a_short_grace_period_still_moves_the_request()
    {
        var (host, fakes) = await StartAsync(null, Grace);
        await using var _host = host;
        fakes.LocalChat = _ => LocalSse("data: {\"error\":{\"message\":\"out of memory\"}}\n\n");

        using var response = await host.Client.SendAsync(Message("s1", "mid", stream: true));

        Assert.Equal("""{"id":"msg","type":"message","model":"upstream-a"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("claude-a", await PinAsync(host, "s1"));
    }
}
