using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Data;
using SimpleRouter.OpenRouter;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using SimpleRouter.Usage;

namespace SimpleRouter.Tests;

public class OpenRouterUpstreamTests
{
    private static UpstreamCall Call(string model = "openrouter/auto", bool stream = false)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var body = JsonNode.Parse("""{"max_tokens":500,"messages":[{"role":"user","content":"hello"}]}""")!.AsObject();
        if (stream) body["stream"] = true;
        return new UpstreamCall(context, body, new SessionIdentity("internal-session-key", "private-client-session"),
            new CatalogModel { Id = "router-entry", Provider = ProviderKind.OpenRouter, UpstreamModelId = model, Enabled = true }, false, 10);
    }

    private static string Body(UpstreamCall call) => Encoding.UTF8.GetString(((MemoryStream)call.Context.Response.Body).ToArray());
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static readonly string Completion = """
        {"id":"gen-test","model":"vendor/resolved-model","choices":[{"finish_reason":"stop","message":{"content":"answer"}}],
        "usage":{"prompt_tokens":20,"completion_tokens":4,"cost":0.001234,"prompt_tokens_details":{"cached_tokens":5,"cache_write_tokens":2}}}
        """;

    private static async Task<RouterHost> Host(FakeHandler handler, bool key = true)
    {
        var host = await RouterHost.StartAsync(services => services.AddHttpClient(OpenRouterUpstream.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler));
        if (key) await host.Services.GetRequiredService<OpenRouterCredentialStore>().SetAsync("test-openrouter-key");
        return host;
    }

    private static Task<UpstreamResult> Send(RouterHost host, UpstreamCall call)
    {
        call.Context.RequestServices = host.Services;
        return ActivatorUtilities.CreateInstance<OpenRouterUpstream>(host.Services).SendAsync(call);
    }

    [Theory]
    [InlineData("openrouter/auto", "auto-router")]
    [InlineData("openrouter/auto-beta", "auto-beta-router")]
    public void Auto_options_use_correct_plugin_and_price_units(string model, string plugin)
    {
        var call = Call(model);
        call.Model.OpenRouter = new OpenRouterRoutingOptions
        {
            CostTier = "xhigh", AllowedModels = ["vendor/*", "other/model"], ExcludedModels = ["vendor/expensive"],
            MaxPromptPrice = 0.75m, MaxCompletionPrice = 2.5m,
        };
        call.Request["output_config"] = new JsonObject { ["effort"] = "low" };
        var translated = OpenRouterRequest.Translate(call);
        Assert.Equal(plugin, translated["plugins"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("xhigh", translated["plugins"]![0]!["cost_tier"]!.GetValue<string>());
        Assert.Equal("vendor/*", translated["plugins"]![0]!["allowed_models"]![0]!.GetValue<string>());
        Assert.Equal("vendor/expensive", translated["plugins"]![0]!["excluded_models"]![0]!.GetValue<string>());
        Assert.Equal(0.75m, translated["provider"]!["max_price"]!["prompt"]!.GetValue<decimal>());
        Assert.Equal(2.5m, translated["provider"]!["max_price"]!["completion"]!.GetValue<decimal>());
        Assert.Equal("low", translated["reasoning"]!["effort"]!.GetValue<string>());
        Assert.False(translated["reasoning"]!["exclude"]!.GetValue<bool>());
    }

    [Fact]
    public void Named_models_omit_auto_plugins_and_session_identity_is_opaque_stable()
    {
        var call = Call("vendor/fixed");
        call.Model.OpenRouter = new OpenRouterRoutingOptions { MaxPromptPrice = 1m };
        var first = OpenRouterRequest.Translate(call);
        var second = OpenRouterRequest.Translate(call);
        Assert.Null(first["plugins"]);
        Assert.NotNull(first["provider"]);
        Assert.Null(first["user"]);
        Assert.Matches("^[0-9a-f]{64}$", first["session_id"]!.GetValue<string>());
        Assert.Equal(first["session_id"]!.ToString(), second["session_id"]!.ToString());
        Assert.DoesNotContain("private-client-session", first.ToJsonString());
        Assert.DoesNotContain("internal-session-key", first.ToJsonString());
        Assert.NotEqual(first["session_id"]!.ToString(), OpenRouterRequest.Translate(call with { Session = new("different", null) })["session_id"]!.ToString());
    }

    [Fact]
    public void Local_caps_and_kwargs_are_not_used_and_effort_maps_independently()
    {
        var call = Call();
        call.Model.ContextWindow = 12;
        call.Model.Effort = new EffortMapping(EffortMode.ChatTemplateKwargs, [EffortLevel.Low, EffortLevel.High],
            new() { ["high"] = new JsonObject { ["enable_thinking"] = true } });
        call.Request["output_config"] = new JsonObject { ["effort"] = "max" };
        var translated = OpenRouterRequest.Translate(call);
        Assert.Equal(500, translated["max_tokens"]!.GetValue<int>());
        Assert.Equal("high", translated["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Null(translated["chat_template_kwargs"]);
        Assert.Null(translated["reasoning_effort"]);
    }

    [Fact]
    public async Task Nonstream_uses_provider_key_and_captures_raw_usage_model_and_generation()
    {
        var handler = new FakeHandler { Respond = _ => Response(Completion) };
        await using var host = await Host(handler);
        var call = Call();
        call.Model.BaseUrl = "http://must-not-be-used.test";
        call.Model.ProtectedApiKey = "not-a-provider-key";
        Assert.True((await Send(host, call)).Written);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://openrouter.test/api/v1/chat/completions", request.Uri.ToString());
        Assert.Equal("Bearer test-openrouter-key", request.Headers["authorization"]);
        var result = JsonNode.Parse(Body(call))!;
        Assert.Equal("router-entry", result["model"]!.GetValue<string>());
        Assert.Equal("answer", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(13, result["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.Equal(5, result["usage"]!["cache_read_input_tokens"]!.GetValue<int>());
        Assert.Equal(2, result["usage"]!["cache_creation_input_tokens"]!.GetValue<int>());
        Assert.Equal(new UpstreamUsageMetadata("vendor/resolved-model", "gen-test", 0.001234m), UpstreamUsageMetadata.Get(call.Context));
    }

    [Fact]
    public async Task Missing_key_does_not_send_and_count_tokens_needs_no_key()
    {
        var handler = new FakeHandler();
        await using var host = await Host(handler, key: false);
        var call = Call();
        var result = await Send(host, call);
        Assert.False(result.Written);
        Assert.Equal(FaultKind.AuthRejected, result.Fault!.Kind);
        Assert.Equal("", Body(call));
        Assert.True((await Send(host, call with { CountTokens = true })).Written);
        Assert.Equal(10, JsonNode.Parse(Body(call))!["input_tokens"]!.GetValue<int>());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(401, FaultKind.AuthRejected)]
    [InlineData(402, FaultKind.AuthRejected)]
    [InlineData(403, FaultKind.AuthRejected)]
    [InlineData(404, FaultKind.PlanRejected)]
    [InlineData(429, FaultKind.Limited)]
    [InlineData(503, FaultKind.Overloaded)]
    public async Task Retryable_status_leaves_client_untouched(int status, FaultKind fault)
    {
        var handler = new FakeHandler { Respond = _ => Response("private body", (HttpStatusCode)status) };
        await using var host = await Host(handler);
        var call = Call();
        var result = await Send(host, call);
        Assert.False(result.Written);
        Assert.Equal(fault, result.Fault!.Kind);
        Assert.Equal("", Body(call));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http200_errors_before_output_fail_over_without_echoing_body(bool streaming)
    {
        const string error = """{"error":{"code":429,"message":"private error text"}}""";
        var handler = new FakeHandler { Respond = _ => Response(streaming ? $"data: {error}\n\n" : error) };
        await using var host = await Host(handler);
        var call = Call(stream: streaming);
        var result = await Send(host, call);
        Assert.False(result.Written);
        Assert.Equal(FaultKind.Limited, result.Fault!.Kind);
        Assert.Equal("", Body(call));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"delta\":{\"content\":\"\"},\"finish_reason\":\"stop\"}]")]
    public async Task Streaming_consumes_final_usage_after_finish_reason(string terminalChoices)
    {
        var sse = "data: {\"id\":\"gen-stream\",\"model\":\"vendor/resolved\",\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\n"
            + $"data: {{\"choices\":{terminalChoices},\"usage\":{{\"prompt_tokens\":30,\"completion_tokens\":8,\"cost\":0.004}}}}\n\n"
            + "data: [DONE]\n\n";
        var handler = new FakeHandler { Respond = _ => Response(sse) };
        await using var host = await Host(handler);
        var call = Call(stream: true);
        Assert.True((await Send(host, call)).Written);
        Assert.Contains("event: message_stop", Body(call));
        var terminal = Events(Body(call)).Single(e => e["type"]!.GetValue<string>() == "message_delta");
        Assert.Equal(8, terminal["usage"]!["output_tokens"]!.GetValue<int>());
        Assert.Equal(30, terminal["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.Equal(new UpstreamUsageMetadata("vendor/resolved", "gen-stream", 0.004m), UpstreamUsageMetadata.Get(call.Context));
        var sent = JsonNode.Parse(Assert.Single(handler.Requests).Body)!;
        Assert.True(sent["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Stream_error_after_output_is_terminal_and_never_echoes_provider_text()
    {
        var handler = new FakeHandler { Respond = _ => Response("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: {\"error\":{\"code\":503,\"message\":\"private body\"}}\n\n") };
        await using var host = await Host(handler);
        var call = Call(stream: true);
        Assert.True((await Send(host, call)).Written);
        Assert.Contains("event: error", Body(call));
        Assert.DoesNotContain("event: message_stop", Body(call));
        Assert.DoesNotContain("private body", Body(call));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structured_reasoning_roundtrips_with_tools_in_order(bool streaming)
    {
        const string details = """[{"type":"reasoning.text","text":"thought","signature":"signed","id":"r1","format":"anthropic-claude-v1","index":0},{"type":"reasoning.encrypted","data":"opaque","id":"r2","format":"openai-responses-v1","index":1},{"type":"reasoning.summary","summary":"summary","id":"r3","index":2}]""";
        var message = JsonNode.Parse("""{"content":"answer","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{\"q\":1}"}}]}""")!.AsObject();
        message["reasoning_details"] = JsonNode.Parse(details);
        var wire = streaming
            ? $"data: {{\"choices\":[{{\"delta\":{message.ToJsonString()},\"finish_reason\":\"tool_calls\"}}]}}\n\ndata: [DONE]\n\n"
            : $"{{\"choices\":[{{\"message\":{message.ToJsonString()},\"finish_reason\":\"tool_calls\"}}]}}";
        var handler = new FakeHandler { Respond = _ => Response(wire) };
        await using var host = await Host(handler);
        var call = Call(stream: streaming);
        Assert.True((await Send(host, call)).Written);
        JsonArray blocks = streaming ? StreamBlocks(Body(call)) : JsonNode.Parse(Body(call))!["content"]!.AsArray();
        Assert.Contains(blocks, b => b!["type"]!.GetValue<string>() == "redacted_thinking");
        var replay = Replay(blocks);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(details), replay["messages"]![0]!["reasoning_details"]));
        Assert.Equal("lookup", replay["messages"]![0]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("answer", replay["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal(1, JsonNode.Parse(replay["messages"]![0]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>())!["q"]!.GetValue<int>());
        Assert.Equal(streaming ? new[] { "text", "thinking", "redacted_thinking", "thinking", "tool_use" }
            : new[] { "thinking", "redacted_thinking", "thinking", "text", "tool_use" }, blocks.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("tool", replay["messages"]![1]!["role"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Http400_distinguishes_context_overflow_from_invalid_request(bool overflow)
    {
        var error = overflow ? "context_length_exceeded" : "invalid_parameter";
        var handler = new FakeHandler { Respond = _ => Response(new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = error, ["message"] = "private request contents" },
        }.ToJsonString(), HttpStatusCode.BadRequest) };
        await using var host = await Host(handler);
        var call = Call();
        call.Context.RequestServices = host.Services;
        var result = await Send(host, call);
        Assert.Equal(!overflow, result.Written);
        if (overflow)
        {
            Assert.Equal(FaultKind.ContextOverflow, result.Fault!.Kind);
            Assert.Null(result.Fault.Message);
            Assert.Equal("", Body(call));
        }
        else Assert.Equal(400, call.Context.Response.StatusCode);
        Assert.DoesNotContain("private request contents", Body(call));
    }

    [Fact]
    public async Task Identified_stream_reasoning_fragments_reconstruct_signed_and_encrypted_entries()
    {
        var fragments = new[]
        {
            """[{"type":"reasoning.text","text":"first ","id":"r1","index":0,"format":"anthropic-claude-v1"},{"type":"reasoning.encrypted","data":"part-","id":"r2","index":1}]""",
            """[{"type":"reasoning.text","text":"second","id":"r1","index":0},{"type":"reasoning.encrypted","data":"two","id":"r2","index":1}]""",
            """[{"signature":"signed","id":"r1","index":0}]""",
        };
        var chunks = fragments.Select(f => new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["reasoning_details"] = JsonNode.Parse(f) } }),
        });
        var separator = new string((char)10, 2);
        var wire = string.Concat(chunks.Select(c => "data: " + c.ToJsonString() + separator))
            + "data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop", ["delta"] = new JsonObject() }) }.ToJsonString()
            + separator + "data: [DONE]" + separator;
        var handler = new FakeHandler { Respond = _ => Response(wire) };
        await using var host = await Host(handler);
        var call = Call(stream: true);
        Assert.True((await Send(host, call)).Written);
        var details = Replay(StreamBlocks(Body(call)))["messages"]![0]!["reasoning_details"]!.AsArray();
        Assert.Equal(2, details.Count);
        Assert.Equal("first second", details[0]!["text"]!.GetValue<string>());
        Assert.Equal("signed", details[0]!["signature"]!.GetValue<string>());
        Assert.Equal("anthropic-claude-v1", details[0]!["format"]!.GetValue<string>());
        Assert.Equal("part-two", details[1]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_truncated_error_body_leaves_response_unwritten_for_failover()
    {
        var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StreamContent(new BrokenBody()) } };
        await using var host = await Host(handler);
        var call = Call();
        var result = await Send(host, call);
        Assert.False(result.Written);
        Assert.Equal(FaultKind.Unreachable, result.Fault!.Kind);
        Assert.Equal("", Body(call));
    }

    private sealed class BrokenBody : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("response reset"));
    }

    [Fact]
    public async Task Unreachable_provider_leaves_response_unwritten()
    {
        var handler = new FakeHandler { Respond = _ => throw new HttpRequestException("private endpoint") };
        await using var host = await Host(handler);
        var call = Call();
        var result = await Send(host, call);
        Assert.False(result.Written);
        Assert.Equal(FaultKind.Unreachable, result.Fault!.Kind);
        Assert.Equal("", Body(call));
    }

    private static JsonObject Replay(JsonArray blocks)
    {
        var next = Call();
        next.Request["messages"] = new JsonArray(new JsonObject { ["role"] = "assistant", ["content"] = blocks.DeepClone() },
            new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "call_1", ["content"] = "result" }) });
        return OpenRouterRequest.Translate(next);
    }

    private static JsonArray StreamBlocks(string sse)
    {
        var blocks = new JsonArray();
        var toolJson = new Dictionary<int, string>();
        foreach (var frame in Events(sse))
        {
            var kind = frame["type"]!.GetValue<string>();
            if (kind == "content_block_start")
            {
                Assert.Equal(blocks.Count, frame["index"]!.GetValue<int>());
                blocks.Add(frame["content_block"]!.DeepClone());
            }
            else if (kind == "content_block_delta")
            {
                var index = frame["index"]!.GetValue<int>();
                var block = blocks[index]!;
                var delta = frame["delta"]!;
                var type = delta["type"]!.GetValue<string>();
                if (type == "input_json_delta")
                    toolJson[index] = toolJson.GetValueOrDefault(index, "") + delta["partial_json"]!.GetValue<string>();
                else
                {
                    var field = type switch { "text_delta" => "text", "thinking_delta" => "thinking", "signature_delta" => "signature", _ => throw new InvalidOperationException(type) };
                    block[field] = (block[field]?.GetValue<string>() ?? "") + delta[field]!.GetValue<string>();
                }
            }
            else if (kind == "content_block_stop")
            {
                var index = frame["index"]!.GetValue<int>();
                if (toolJson.TryGetValue(index, out var json)) blocks[index]!["input"] = JsonNode.Parse(json);
            }
        }
        return blocks;
    }
    private static IEnumerable<JsonObject> Events(string sse) => sse.Split('\n').Where(l => l.StartsWith("data: {", StringComparison.Ordinal))
        .Select(l => JsonNode.Parse(l[6..])!.AsObject());
}
