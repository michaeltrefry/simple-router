using System.Net;
using System.Net.Http.Headers;
using System.IO.Pipelines;
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
            { "id": "local-off", "provider": "local", "upstreamModelId": "upstream-local-off", "enabled": false, "baseUrl": "http://local-off.test/v1" },
            { "id": "local-c", "provider": "local", "upstreamModelId": "upstream-local-c", "enabled": true, "baseUrl": "http://local-c.test/v1", "supportsTools": false }
          ],
          "tiers": {
            "high": [ "sub-mid", "local-a" ],
            "mid": [ "sub-mid", "local-off", "local-a", "local-b" ],
            "low": [ "sub-mid", "local-b", "local-c" ]
          },
          "aliases": [ { "pattern": "*sonnet*", "tier": "mid" }, { "pattern": "*haiku*", "tier": "low" } ]
        }
        """;

    private sealed class Locals
    {
        public HashSet<string> Healthy { get; } = ["local-a.test", "local-b.test", "local-c.test", "local-off.test"];
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
        var locals = new Locals();
        var local = new FakeHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/models")
                ? new HttpResponseMessage(locals.Healthy.Contains(request.RequestUri.Host) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                : locals.Chat(request),
        };
        // No explicit health check: the router probes before it serves.
        var host = await RouterHost.StartInAsync(dataDirectory ?? RouterHost.NewDataDirectory(), local, [$"--SimpleRouter:SeedFile={seedPath}", .. extraArgs]);
        await host.LoginAsync();
        host.Upstream.Respond = _ => Json("""{"id":"msg_sub","type":"message"}""");
        await host.AuthorizedClientAsync();
        return (host, locals);
    }

    private static Task CheckHealthAsync(RouterHost host) => host.Services.GetRequiredService<LocalHealth>().CheckAsync(CancellationToken.None);

    private static HttpRequestMessage Message(string model = "claude-sonnet-5-5", string? sessionId = null, JsonArray? messages = null, bool stream = false,
        string path = "/v1/messages", JsonArray? tools = null, string? modelClass = null, int maxTokens = 64)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
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
        Assert.Equal(["http://local-a.test/v1/models", "http://local-b.test/v1/models", "http://local-c.test/v1/models"], probes.Order());
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
        Assert.Equal(9, message.Usage!["output_tokens"]!.GetValue<int>());
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
        Directory.CreateDirectory(Path.GetDirectoryName(overridePath)!);
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

    private static HttpResponseMessage Sse(string body) => Sse(new StringContent(body));

    private static HttpResponseMessage Sse(HttpContent content)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private const string PartialChunk = """data: {"choices":[{"index":0,"delta":{"content":"partial"}}]}""";

    [Fact]
    public async Task A_prompt_that_leaves_too_little_of_a_context_window_for_the_requested_output_skips_that_local()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        // About 800 estimated tokens against local-a's 1000-token window, with 500 tokens of output requested.
        var prompt = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = new string('x', 3100) } };

        Assert.Equal("local-b.test", await ServedByAsync(host, Message(sessionId: "tight", messages: prompt, maxTokens: 500)));
        Assert.Equal(500, JsonNode.Parse(ChatRequests(host)[^1].Body)!["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Requests_with_tools_skip_locals_without_tool_support()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var tools = JsonNode.Parse("""[{"name":"Read","input_schema":{"type":"object"}}]""")!.AsArray();

        var withTools = new List<string>();
        foreach (var session in new[] { "t1", "t2", "t3" })
        {
            withTools.Add(await ServedByAsync(host, Message("claude-haiku-5-5", sessionId: session, tools: (JsonArray)tools.DeepClone())));
        }
        // The low cursor is now 3, so a tool-less request's turn falls on local-c.
        var withoutTools = await ServedByAsync(host, Message("claude-haiku-5-5", sessionId: "t4"));

        Assert.Equal(["local-b.test", "local-b.test", "local-b.test"], withTools);
        Assert.Equal("local-c.test", withoutTools);
    }

    [Fact]
    public async Task A_pinned_local_session_keeps_its_model_across_a_restart()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var (first, _) = await StartAsync(dataDirectory);
        Assert.Equal("local-a.test", await ServedByAsync(first, Message(sessionId: "kept")));
        first.KeepData = true;
        await first.DisposeAsync();

        var (host, _) = await StartAsync(dataDirectory);
        await using var _host = host;

        Assert.Equal("local-a.test", await ServedByAsync(host, Message(sessionId: "kept")));
    }

    [Theory]
    [InlineData("eof without a finish reason", PartialChunk + "\n\n", "Local model stream ended before completion.")]
    [InlineData("malformed chunk", PartialChunk + "\n\ndata: {\"choices\":[\n\n", "Local model sent a malformed stream chunk.")]
    [InlineData("mid-stream error", PartialChunk + "\n\ndata: {\"error\":{\"message\":\"out of memory\"}}\n\n", "out of memory")]
    public async Task A_local_stream_that_fails_ends_in_an_error_event_without_message_stop(string _, string upstream, string error)
    {
        var (host, locals) = await StartAsync();
        await using var _host = host;
        locals.Chat = _ => Sse(upstream);

        using var response = await host.Client.SendAsync(Message(sessionId: "s1", stream: true));

        var message = AnthropicSse.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(error, message.Error);
        Assert.Equal("partial", Assert.Single(message.Blocks).Text);
        Assert.DoesNotContain("message_stop", message.Events);
    }

    [Theory]
    [InlineData("crlf and keepalives", ": keepalive\r\n\r\ndata: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"}}]}\r\n\r\n: keepalive\r\n\r\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\r\n\r\ndata: [DONE]\r\n\r\n")]
    [InlineData("finish then eof without done", "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"}}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n")]
    [InlineData("multi-line data event", "data: {\"choices\":[{\"index\":0,\ndata: \"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")]
    public async Task Local_stream_framing_variants_produce_a_complete_anthropic_stream(string _, string upstream)
    {
        var (host, locals) = await StartAsync();
        await using var _host = host;
        locals.Chat = _ => Sse(upstream);

        using var response = await host.Client.SendAsync(Message(sessionId: "s1", stream: true));

        var message = AnthropicSse.Parse(await response.Content.ReadAsStringAsync());
        Assert.Null(message.Error);
        Assert.Equal("hi", Assert.Single(message.Blocks).Text);
        Assert.Equal("end_turn", message.StopReason);
    }

    [Fact]
    public async Task A_local_stream_that_goes_quiet_is_ended_with_an_error_after_the_idle_timeout()
    {
        var (host, locals) = await StartAsync(null, "--SimpleRouter:Local:StreamIdleTimeout=00:00:00.300");
        await using var _host = host;
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(PartialChunk + "\n\n"));
        locals.Chat = _ => Sse(new StreamContent(pipe.Reader.AsStream()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await host.Client.SendAsync(Message(sessionId: "s1", stream: true), deadline.Token);

        var message = AnthropicSse.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal("Local model sent nothing for 0.3 s.", message.Error);
        Assert.Equal("partial", Assert.Single(message.Blocks).Text);
    }

    private sealed class EndlessStream : Stream
    {
        private const long Limit = 1024 * 1024;
        private long _read;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read > Limit)
            {
                throw new IOException("read far past the error body limit");
            }
            buffer.AsSpan(offset, count).Fill((byte)' ');
            _read += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task An_endless_error_body_is_read_only_up_to_the_limit()
    {
        var (host, locals) = await StartAsync();
        await using var _host = host;
        locals.Chat = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StreamContent(new EndlessStream()) };

        using var response = await host.Client.SendAsync(Message(sessionId: "s1"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("Local model local-a returned HTTP 500.", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["message"]!.GetValue<string>());
    }
}
