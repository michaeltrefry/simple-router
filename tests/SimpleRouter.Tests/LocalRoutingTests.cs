using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Data;
using SimpleRouter.Local;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class LocalRoutingTests
{
    private const string Seed = """
        {
          "defaultTier": "high",
          "models": [
            { "id": "sub-mid", "provider": "claude", "upstreamModelId": "upstream-sub", "enabled": true },
            { "id": "local-a", "provider": "local", "upstreamModelId": "upstream-local-a", "enabled": true,
              "baseUrl": "http://local-a.test/v1", "apiKey": "local-secret-a", "contextWindow": 1000, "supportsVision": true },
            { "id": "local-b", "provider": "local", "upstreamModelId": "upstream-local-b", "enabled": true, "baseUrl": "http://local-b.test/v1/" },
            { "id": "local-off", "provider": "local", "upstreamModelId": "upstream-local-off", "enabled": false, "baseUrl": "http://local-off.test/v1" }
          ],
          "tiers": {
            "high": [ "sub-mid", "local-a" ],
            "mid": [ "sub-mid", "local-off", "local-a", "local-b" ],
            "low": [ "sub-mid", "local-b" ]
          },
          "aliases": [ { "pattern": "*sonnet*", "tier": "mid" }, { "pattern": "*haiku*", "tier": "low" } ]
        }
        """;

    private sealed class Locals
    {
        public HashSet<string> Healthy { get; } = ["local-a.test", "local-b.test", "local-off.test"];
        public Func<HttpRequestMessage, HttpResponseMessage> Chat { get; set; } = _ => Json(TextCompletion("hi from local"));
    }

    private static string TextCompletion(string text) => new JsonObject
    {
        ["id"] = "chatcmpl-1",
        ["choices"] = new JsonArray { new JsonObject { ["index"] = 0, ["finish_reason"] = "stop", ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text } } },
        ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 3 },
    }.ToJsonString();

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<(RouterHost Host, Locals Locals)> StartAsync(string? dataDirectory = null, params string[] extraArgs)
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, Seed);
        var host = await RouterHost.StartInAsync(dataDirectory ?? RouterHost.NewDataDirectory(), [$"--SimpleRouter:SeedFile={seedPath}", .. extraArgs]);
        await host.LoginAsync();
        host.Upstream.Respond = _ => Json("""{"id":"msg_sub","type":"message"}""");
        var locals = new Locals();
        host.Local.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/models")
            ? new HttpResponseMessage(locals.Healthy.Contains(request.RequestUri.Host) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            : locals.Chat(request);
        await host.AuthorizedClientAsync();
        await CheckHealthAsync(host);
        return (host, locals);
    }

    private static Task CheckHealthAsync(RouterHost host) => host.Services.GetRequiredService<LocalHealth>().CheckAsync(CancellationToken.None);

    private static HttpRequestMessage Message(string model = "claude-sonnet-5-5", string? sessionId = null, JsonArray? messages = null, bool stream = false,
        string path = "/v1/messages", JsonArray? tools = null, string? modelClass = null)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 64,
            ["system"] = "Be brief.",
            ["messages"] = messages ?? new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hello" } },
        };
        if (stream)
        {
            body["stream"] = true;
        }
        if (tools is not null)
        {
            body["tools"] = tools;
        }
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (sessionId is not null)
        {
            request.Headers.Add(SessionKey.HeaderName, sessionId);
        }
        if (modelClass is not null)
        {
            request.Headers.Add(TierResolver.HeaderName, modelClass);
        }
        return request;
    }

    /// <summary>Sends and returns which upstream served it: a local base host or "claude".</summary>
    private static async Task<string> ServedByAsync(RouterHost host, HttpRequestMessage request)
    {
        var claudeBefore = host.Upstream.Requests.Count;
        var localBefore = ChatRequests(host).Count;
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (host.Upstream.Requests.Count > claudeBefore)
        {
            return "claude";
        }
        Assert.Equal(localBefore + 1, ChatRequests(host).Count);
        return ChatRequests(host)[^1].Uri.Host;
    }

    private static List<FakeHandler.Recorded> ChatRequests(RouterHost host)
    {
        lock (host.Local.Requests)
        {
            return host.Local.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/chat/completions")).ToList();
        }
    }

    [Fact]
    public async Task New_mid_and_low_sessions_go_to_healthy_locals_before_subscriptions()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;

        var mid = new List<string>();
        foreach (var session in new[] { "s1", "s2", "s3" })
        {
            mid.Add(await ServedByAsync(host, Message(sessionId: session)));
        }
        var low = await ServedByAsync(host, Message("claude-haiku-5-5", sessionId: "s4"));
        var high = await ServedByAsync(host, Message("x", sessionId: "s5", modelClass: "high"));

        Assert.Equal(["local-a.test", "local-b.test", "local-a.test"], mid);
        Assert.Equal("local-b.test", low);
        Assert.Equal("claude", high);
        Assert.Equal(mid[0], await ServedByAsync(host, Message(sessionId: "s1")));
    }

    [Fact]
    public async Task When_every_local_fails_its_health_check_new_sessions_use_a_subscription()
    {
        var (host, locals) = await StartAsync();
        await using var _host = host;
        Assert.Equal("local-a.test", await ServedByAsync(host, Message(sessionId: "pinned-local")));

        locals.Healthy.Clear();
        await CheckHealthAsync(host);

        Assert.Equal("claude", await ServedByAsync(host, Message(sessionId: "fresh")));
        Assert.Equal("claude", await ServedByAsync(host, Message(sessionId: "pinned-local")));
        var sent = JsonDocument.Parse(host.Upstream.Requests[^1].Body);
        Assert.Equal("upstream-sub", sent.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task An_unhealthy_local_is_excluded_until_it_recovers()
    {
        var (host, locals) = await StartAsync();
        await using var _host = host;
        locals.Healthy.Remove("local-a.test");
        await CheckHealthAsync(host);

        var whileDown = new[] { await ServedByAsync(host, Message(sessionId: "d1")), await ServedByAsync(host, Message(sessionId: "d2")) };
        locals.Healthy.Add("local-a.test");
        await CheckHealthAsync(host);
        var recovered = new[] { await ServedByAsync(host, Message(sessionId: "r1")), await ServedByAsync(host, Message(sessionId: "r2")) };

        Assert.Equal(["local-b.test", "local-b.test"], whileDown);
        Assert.Contains("local-a.test", recovered);
        var probes = host.Local.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/models")).Select(r => r.Uri.ToString()).ToHashSet();
        Assert.Equal(["http://local-a.test/v1/models", "http://local-b.test/v1/models"], probes.Order());
    }

    [Fact]
    public async Task A_request_larger_than_a_locals_context_window_skips_it()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var big = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = new string('x', 8000) } };

        Assert.Equal("local-b.test", await ServedByAsync(host, Message(sessionId: "big", messages: big)));
    }

    [Fact]
    public async Task Local_requests_carry_the_translated_body_and_the_configured_key()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var gatewayKey = await host.GatewayKeyAsync();

        using var response = await host.Client.SendAsync(Message(sessionId: "s1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("hi from local", message["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("local-a", message["model"]!.GetValue<string>());
        Assert.Equal("""{"input_tokens":10,"output_tokens":3}""", message["usage"]!.ToJsonString());
        var sent = ChatRequests(host).Single();
        Assert.Equal("http://local-a.test/v1/chat/completions", sent.Uri.ToString());
        Assert.Equal("Bearer local-secret-a", sent.Headers["authorization"]);
        Assert.DoesNotContain(gatewayKey, string.Join(" ", sent.Headers.Values));
        var body = JsonNode.Parse(sent.Body)!;
        Assert.Equal("upstream-local-a", body["model"]!.GetValue<string>());
        Assert.Equal("""[{"role":"system","content":"Be brief."},{"role":"user","content":"hello"}]""", body["messages"]!.ToJsonString());

        await ServedByAsync(host, Message(sessionId: "s2"));
        Assert.False(ChatRequests(host)[^1].Headers.ContainsKey("authorization"));
    }

    [Fact]
    public async Task Local_api_keys_are_stored_encrypted()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;

        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        var stored = (await db.Models.SingleAsync(m => m.Id == "local-a")).ProtectedApiKey;
        Assert.NotNull(stored);
        Assert.DoesNotContain("local-secret-a", stored);
        Assert.Equal("local-secret-a", host.Services.GetRequiredService<SecretProtector>().Unprotect(stored));
    }

    [Fact]
    public async Task A_streamed_text_and_tool_call_arrives_as_a_valid_anthropic_stream()
    {
        var (host, locals) = await StartAsync();
        await using var _host = host;
        locals.Chat = _ =>
        {
            var sse = string.Concat(new[]
            {
                """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","content":"Checking."}}]}""",
                """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_7","type":"function","function":{"name":"Read","arguments":"{\"path\""}}]}}]}""",
                """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":":\"a.txt\"}"}}]}}]}""",
                """{"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
                """{"id":"c1","choices":[],"usage":{"prompt_tokens":40,"completion_tokens":9}}""",
            }.Select(chunk => $"data: {chunk}\n\n")) + "data: [DONE]\n\n";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        };

        using var response = await host.Client.SendAsync(Message(sessionId: "s1", stream: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var message = AnthropicSse.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Checking.", message.Blocks[0].Text);
        var tool = message.Blocks[1];
        Assert.Equal(("tool_use", "call_7", "Read"), (tool.Type, tool.Id, tool.Name));
        Assert.Equal("""{"path":"a.txt"}""", tool.Input!.ToJsonString());
        Assert.Equal("tool_use", message.StopReason);
        Assert.Equal(9, message.Usage["output_tokens"]!.GetValue<int>());
        var sent = JsonNode.Parse(ChatRequests(host).Single().Body)!;
        Assert.True(sent["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_tool_result_reaches_the_local_endpoint_as_a_tool_message_with_the_matching_call_id()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "hello" },
            JsonNode.Parse("""{"role":"assistant","content":[{"type":"tool_use","id":"call_7","name":"Read","input":{"path":"a.txt"}}]}"""),
            JsonNode.Parse("""{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_7","content":"file body"}]}"""),
        };

        Assert.Equal("local-a.test", await ServedByAsync(host, Message(sessionId: "s1", messages: messages)));

        var sent = JsonNode.Parse(ChatRequests(host).Single().Body)!["messages"]!.AsArray();
        Assert.Equal("""{"role":"tool","tool_call_id":"call_7","content":"file body"}""", sent[^1]!.ToJsonString());
        Assert.Equal("call_7", sent[^2]!["tool_calls"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Count_tokens_for_a_local_model_is_estimated_without_an_upstream_call()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;

        using var response = await host.Client.SendAsync(Message(sessionId: "s1", path: "/v1/messages/count_tokens"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["input_tokens"]!.GetValue<int>();
        Assert.InRange(tokens, 5, 40);
        Assert.Empty(ChatRequests(host));
        Assert.Empty(host.Upstream.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"context too long","type":"invalid_request_error"}}""", 400, "invalid_request_error", "context too long")]
    [InlineData(HttpStatusCode.TooManyRequests, "busy", 429, "rate_limit_error", "Local model local-a returned HTTP 429.")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}""", 502, "api_error", "bad key")]
    [InlineData(HttpStatusCode.InternalServerError, "<html>oops</html>", 502, "api_error", "Local model local-a returned HTTP 500.")]
    public async Task Local_upstream_errors_become_anthropic_errors(HttpStatusCode upstreamStatus, string upstreamBody, int status, string type, string message)
    {
        var (host, locals) = await StartAsync();
        await using var _host = host;
        locals.Chat = _ => new HttpResponseMessage(upstreamStatus) { Content = new StringContent(upstreamBody) };

        using var response = await host.Client.SendAsync(Message(sessionId: "s1", stream: true));

        Assert.Equal(status, (int)response.StatusCode);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("error", error["type"]!.GetValue<string>());
        Assert.Equal(type, error["error"]!["type"]!.GetValue<string>());
        Assert.Equal(message, error["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unreachable_local_is_a_502_and_is_marked_unhealthy()
    {
        var (host, locals) = await StartAsync();
        await using var _host = host;
        locals.Chat = _ => throw new HttpRequestException("connection refused");

        using var response = await host.Client.SendAsync(Message(sessionId: "s1"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("api_error", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["type"]!.GetValue<string>());
        Assert.False(host.Services.GetRequiredService<LocalHealth>().IsHealthy("local-a"));
    }

    [Fact]
    public async Task The_local_override_file_points_seeded_models_at_real_endpoints_on_every_start()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var overridePath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"override-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(overridePath, """
            { "models": [ { "id": "local-off", "enabled": true, "baseUrl": "http://override.test/v1", "upstreamModelId": "real-id", "apiKey": "override-key", "contextWindow": 5000 } ] }
            """);
        var (first, _) = await StartAsync(dataDirectory);
        first.KeepData = true;
        await first.DisposeAsync();

        var (host, locals) = await StartAsync(dataDirectory, $"--SimpleRouter:SeedOverrideFile={overridePath}");
        await using var _host = host;
        locals.Healthy.Add("override.test");
        await CheckHealthAsync(host);

        Assert.Equal("override.test", await ServedByAsync(host, Message(sessionId: "s1")));
        var sent = ChatRequests(host).Single();
        Assert.Equal("Bearer override-key", sent.Headers["authorization"]);
        Assert.Equal("real-id", JsonNode.Parse(sent.Body)!["model"]!.GetValue<string>());
    }
}
