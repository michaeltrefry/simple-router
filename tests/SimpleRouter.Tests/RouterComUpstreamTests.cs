using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Codex;
using SimpleRouter.Data;
using SimpleRouter.RouterCom;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using SimpleRouter.Usage;
using static SimpleRouter.Tests.Support.ResponsesFixtures;

namespace SimpleRouter.Tests;

public class RouterComUpstreamTests
{
    private static UpstreamCall Call(bool streaming = false)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var body = JsonNode.Parse("""{"model":"default","max_tokens":500,"messages":[{"role":"user","content":"hello"}]}""")!.AsObject();
        body["stream"] = streaming;
        return new(context, body, new("private-routing-session", "private-client-session"),
            new CatalogModel { Id = "router-entry", Provider = ProviderKind.RouterCom, UpstreamModelId = "test-model", Enabled = true }, false, 10);
    }
    private static string Body(UpstreamCall call) => Encoding.UTF8.GetString(((MemoryStream)call.Context.Response.Body).ToArray());
    private static async Task<RouterHost> Host(FakeHandler handler, bool key = true)
    {
        var host = await RouterHost.StartAsync(services => services.AddHttpClient(RouterComUpstream.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler));
        if (key) await host.Services.GetRequiredService<RouterComCredentialStore>().SetAsync("test-routercom-key");
        return host;
    }
    private static Task<UpstreamResult> Send(RouterHost host, UpstreamCall call)
    {
        call.Context.RequestServices = host.Services;
        return ActivatorUtilities.CreateInstance<RouterComUpstream>(host.Services).SendAsync(call);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void Flex_controls_are_explicit_independent_of_effort_and_preserve_generation_limits(bool? flex)
    {
        var call = Call();
        call.Model.RouterCom = new() { AllowFlexTier = flex, FallbackTimeoutSeconds = 30, ProviderTimeoutSeconds = 140 };
        call.Model.Effort = new(EffortMode.ReasoningEffort, [EffortLevel.Low, EffortLevel.High]);
        call.Request["output_config"] = new JsonObject { ["effort"] = "xhigh" };
        call.Request["temperature"] = .4;
        var translated = RouterComRequest.Translate(call);
        Assert.Equal(flex, translated["allow_flex_tier"]?.GetValue<bool>());
        Assert.Equal(30, translated["fallback_timeout"]!.GetValue<int>());
        Assert.Equal(140, translated["provider_timeout"]!.GetValue<int>());
        Assert.Equal("high", translated["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal(500, translated["max_output_tokens"]!.GetValue<int>());
        Assert.Equal(.4, translated["temperature"]!.GetValue<double>());
        Assert.Null(translated["plugins"]);
        Assert.Matches("^[a-f0-9]{64}$", translated["prompt_cache_key"]!.GetValue<string>());
        Assert.DoesNotContain("private-routing-session", translated.ToJsonString());
        Assert.DoesNotContain("private-client-session", translated.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responses_translate_text_reasoning_tools_and_reported_cost(bool streaming)
    {
        var complete = JsonNode.Parse(Completed(input: 120, output: 45, cached: 100, outputItems: """
          [{"type":"reasoning","summary":[{"type":"summary_text","text":"thought"}]},
           {"type":"message","content":[{"type":"output_text","text":"answer"}]},
           {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{}"}]
          """))!.AsObject();
        complete["response"]!["model"] = "actual-model";
        complete["response"]!["usage"]!["cost"] = .00123m;
        var handler = new FakeHandler { Respond = _ => streaming ? Stream(Sse(Created, SummaryDelta(0, 0, "thought"),
            TextDelta(1, "answer"), CallDone(2, "call_1", "lookup", "{}"), complete.ToJsonString()))
            : SettingsHarness.Json(complete["response"]!.ToJsonString()) };
        await using var host = await Host(handler);
        var call = Call(streaming);
        call.Model.RouterCom = new() { AllowFlexTier = true, FallbackTimeoutSeconds = 30 };
        Assert.True((await Send(host, call)).Written);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("http://routercom.test/v1/responses", sent.Uri.ToString());
        Assert.Equal("Bearer test-routercom-key", sent.Headers["authorization"]);
        Assert.DoesNotContain(sent.Headers.Keys, h => h.Equals("ChatGPT-Account-ID", StringComparison.OrdinalIgnoreCase));
        Assert.True(JsonNode.Parse(sent.Body)!["allow_flex_tier"]!.GetValue<bool>());
        Assert.Equal(streaming, JsonNode.Parse(sent.Body)!["stream"]!.GetValue<bool>());
        Assert.Contains("answer", Body(call));
        Assert.Contains("thought", Body(call));
        Assert.Contains("lookup", Body(call));
        Assert.Contains(streaming ? "event: message_stop" : "\"tool_use\"", Body(call));
        Assert.Equal(new UpstreamUsageMetadata("actual-model", "resp_1", .00123m), UpstreamUsageMetadata.Get(call.Context));
        if (streaming) Assert.Equal(1, Body(call).Split("event: message_start").Length - 1);
        else
        {
            var message = JsonNode.Parse(Body(call))!;
            Assert.Equal(20, message["usage"]!["input_tokens"]!.GetValue<int>());
            Assert.Equal(100, message["usage"]!["cache_read_input_tokens"]!.GetValue<int>());
        }
    }

    [Theory]
    [InlineData(401, FaultKind.AuthRejected)]
    [InlineData(402, FaultKind.AuthRejected)]
    [InlineData(403, FaultKind.PlanRejected)]
    [InlineData(404, FaultKind.PlanRejected)]
    [InlineData(429, FaultKind.Limited)]
    [InlineData(501, FaultKind.PlanRejected)]
    [InlineData(503, FaultKind.Overloaded)]
    public async Task Retryable_errors_allow_failover_without_relaying_provider_bodies(int status, FaultKind fault)
    {
        var handler = new FakeHandler { Respond = _ => new((HttpStatusCode)status) { Content = new StringContent("private provider body") } };
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
    public async Task Stream_failures_before_output_fail_over_and_after_output_are_sanitized(bool afterOutput)
    {
        var wire = Sse(Created) + (afterOutput ? Sse(TextDelta(0, "answer")) : "")
            + Sse("""{"type":"response.failed","response":{"error":{"code":"server_error","message":"private provider body"}}}""");
        var handler = new FakeHandler { Respond = _ => Stream(wire) };
        await using var host = await Host(handler);
        var call = Call(streaming: true);
        var result = await Send(host, call);
        Assert.Equal(afterOutput, result.Written);
        Assert.DoesNotContain("private provider body", Body(call));
        Assert.DoesNotContain("event: message_stop", Body(call));
        if (afterOutput) Assert.Contains("event: error", Body(call));
        else Assert.Equal("", Body(call));
    }

    [Fact]
    public async Task Missing_key_and_token_count_never_call_the_provider()
    {
        var handler = new FakeHandler();
        await using var host = await Host(handler, key: false);
        var call = Call();
        Assert.False((await Send(host, call)).Written);
        var count = call with { CountTokens = true };
        Assert.True((await Send(host, count)).Written);
        Assert.Equal(10, JsonNode.Parse(Body(count))!["input_tokens"]!.GetValue<int>());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Routercom_reasoning_replays_only_to_routercom_and_keeps_tools_and_images()
    {
        var call = Call();
        Assert.Null(RouterComRequest.Translate(call)["reasoning"]);
        call.Request["messages"] = JsonNode.Parse("""
        [
          {"role":"assistant","content":[{"type":"thinking","thinking":"thought","signature":"routercom:opaque"},{"type":"tool_use","id":"call_1","name":"lookup","input":{}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"result"},{"type":"image","source":{"type":"url","url":"https://example.test/image.png"}}]}
        ]
        """);
        var translated = RouterComRequest.Translate(call).ToJsonString();
        Assert.Contains("opaque", translated);
        Assert.Contains("function_call_output", translated);
        Assert.Contains("input_image", translated);
        Assert.DoesNotContain("opaque", ResponsesRequestTranslator.Translate(call.Request, call.Model, "cache").ToJsonString());
    }
}
