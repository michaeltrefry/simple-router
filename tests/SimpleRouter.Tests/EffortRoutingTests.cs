using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.ResponsesFixtures;

namespace SimpleRouter.Tests;

/// <summary>One client effort, as each seeded target receives it from the router against fake upstreams.</summary>
public class EffortRoutingTests
{
    private const string Thinking = """{"type":"enabled","budget_tokens":2048}""";

    private static HttpRequestMessage Message(string sessionId, string modelClass, string effort = "high")
    {
        var body = new JsonObject
        {
            ["model"] = "claude-opus-5-5",
            ["max_tokens"] = 4096,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Think hard." } },
            ["thinking"] = JsonNode.Parse(Thinking),
            ["output_config"] = new JsonObject { ["effort"] = effort },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionKey.HeaderName, sessionId);
        request.Headers.Add(TierResolver.HeaderName, modelClass);
        return request;
    }

    /// <summary>The seeded catalog with qwen-3.8-37b enabled by the local override file and given a kwargs mapping.</summary>
    private static async Task<RouterHost> StartAsync(string? dataDirectory = null)
    {
        var overridePath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"override-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        await File.WriteAllTextAsync(overridePath, """
            { "models": [ { "id": "qwen-3.8-37b", "enabled": true, "baseUrl": "http://qwen.test/v1",
                "effort": { "mode": "chat_template_kwargs", "levels": [ "low", "medium" ],
                            "kwargs": { "low": { "enable_thinking": false }, "medium": { "enable_thinking": true, "depth": 2 } } } } ] }
            """);
        var local = new FakeHandler
        {
            Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/models")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"c","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}]}""", Encoding.UTF8, "application/json"),
                },
        };
        var host = await RouterHost.StartInAsync(dataDirectory ?? RouterHost.NewDataDirectory(), local, $"--SimpleRouter:SeedOverrideFile={overridePath}");
        await host.LoginAsync();
        await host.LoginCodexAsync();
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        host.Codex.Respond = _ => Stream(ReasoningTextAndToolCall);
        await host.AuthorizedClientAsync();
        return host;
    }

    private static async Task SendAsync(RouterHost host, HttpRequestMessage request)
    {
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task High_effort_reaches_claude_unchanged_codex_as_reasoning_effort_and_a_local_model_through_its_mapping()
    {
        await using var host = await StartAsync();

        // Round-robin: the tier's first new session goes to Claude, the second to its Codex member.
        await SendAsync(host, Message("claude-session", "high"));
        await SendAsync(host, Message("codex-session", "high"));
        // Low gives new sessions to a healthy local model first.
        await SendAsync(host, Message("local-session", "low"));

        var claude = JsonNode.Parse(Assert.Single(host.Upstream.Requests).Body)!;
        Assert.Equal("""{"effort":"high"}""", claude["output_config"]!.ToJsonString());
        Assert.Equal(Thinking, claude["thinking"]!.ToJsonString());
        Assert.Null(claude["reasoning"]);

        var codex = JsonNode.Parse(Assert.Single(host.Codex.Requests).Body)!;
        Assert.Equal("gpt-6.1-sol", codex["model"]!.GetValue<string>());
        Assert.Equal("""{"summary":"auto","effort":"high"}""", codex["reasoning"]!.ToJsonString());

        var chat = JsonNode.Parse(host.Local.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/chat/completions")).Body)!;
        Assert.Equal("qwen-3.8-37b", chat["model"]!.GetValue<string>());
        // high is not listed for this model; medium is the nearest level it accepts.
        Assert.Equal("""{"enable_thinking":true,"depth":2}""", chat["chat_template_kwargs"]!.ToJsonString());
        Assert.Null(chat["reasoning_effort"]);
    }

    [Fact]
    public async Task Max_effort_reaches_gpt_6_astra_as_max_and_other_codex_models_as_xhigh()
    {
        await using var host = await StartAsync();

        await SendAsync(host, Message("claude-max", "max", "max"));
        await SendAsync(host, Message("astra", "max", "max"));
        await SendAsync(host, Message("claude-high", "high", "max"));
        await SendAsync(host, Message("sol", "high", "max"));

        Assert.Equal(
            [("gpt-6-astra", "max"), ("gpt-6.1-sol", "xhigh")],
            host.Codex.Requests.Select(r => JsonNode.Parse(r.Body)!).Select(b => (b["model"]!.GetValue<string>(), b["reasoning"]!["effort"]!.GetValue<string>())));
    }

    [Fact]
    public async Task A_catalog_seeded_without_effort_mappings_takes_them_from_the_seed_and_keeps_its_own()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var first = await StartAsync(dataDirectory);
        first.KeepData = true;
        await using (var db = await first.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync())
        {
            (await db.Models.FindAsync("gpt-6-astra"))!.Effort = null;
            (await db.Models.FindAsync("gpt-6-luna"))!.Effort = new EffortMapping(Levels: [EffortLevel.Low]);
            await db.SaveChangesAsync();
        }
        await first.DisposeAsync();

        await using var host = await StartAsync(dataDirectory);
        await using var reread = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        Assert.Equal(EffortLevel.Max, (await reread.Models.FindAsync("gpt-6-astra"))!.Effort!.Levels![^1]);
        Assert.Equal([EffortLevel.Low], (await reread.Models.FindAsync("gpt-6-luna"))!.Effort!.Levels);
        Assert.Null((await reread.Models.FindAsync("gpt-6.1-sol"))!.Effort);
    }
}
