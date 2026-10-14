using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class ModelRoutingTests
{
    private const string TwoModelMidSeed = """
        {
          "defaultTier": "high",
          "models": [
            { "id": "model-a", "provider": "claude", "upstreamModelId": "upstream-a", "enabled": true },
            { "id": "model-b", "provider": "claude", "upstreamModelId": "upstream-b", "enabled": true },
            { "id": "model-off", "provider": "claude", "upstreamModelId": "upstream-off", "enabled": false }
          ],
          "tiers": { "mid": [ "model-a", "model-off", "model-b" ], "high": [ "model-b" ] },
          "aliases": [ { "pattern": "*sonnet*", "tier": "mid" } ]
        }
        """;

    private static HttpRequestMessage Message(string model, string firstUser = "hello", string? sessionId = null, string? modelClass = null, string path = "/v1/messages")
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 16,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = firstUser } },
        };
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

    private static async Task<RouterHost> StartLoggedInAsync(string dataDirectory, string? seed = null)
    {
        string[] args = [];
        if (seed is not null)
        {
            var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
            await File.WriteAllTextAsync(seedPath, seed);
            args = [$"--SimpleRouter:SeedFile={seedPath}"];
        }
        var host = await RouterHost.StartInAsync(dataDirectory, args);
        await host.LoginAsync();
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        await host.AuthorizedClientAsync();
        return host;
    }

    private static async Task<(string Model, string? ModelClass)> SendAsync(RouterHost host, HttpRequestMessage request)
    {
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var sent = JsonDocument.Parse(host.Upstream.Requests[^1].Body);
        return (sent.RootElement.GetProperty("model").GetString()!,
            response.Headers.TryGetValues(MessageRouter.ModelClassHeader, out var v) ? v.Single() : null);
    }

    [Theory]
    [InlineData("claude-sonnet-5-5", "high", "claude-opus-5-5", "high")]
    [InlineData("claude-sonnet-5-5", "MAX", "claude-fable-5-1", "max")]
    [InlineData("claude-sonnet-5-5", null, "claude-sonnet-5-5", "mid")]
    [InlineData("claude-haiku-4-5", null, "claude-haiku-5-5", "low")]
    [InlineData("default", null, "claude-opus-5-5", "high")]
    public async Task Seeded_tiers_route_and_label_the_response(string model, string? modelClass, string expectedModel, string expectedClass)
    {
        await using var host = await StartLoggedInAsync(RouterHost.NewDataDirectory());

        var (sentModel, served) = await SendAsync(host, Message(model, modelClass: modelClass));

        Assert.Equal(expectedModel, sentModel);
        Assert.Equal(expectedClass, served);
    }

    [Fact]
    public async Task An_invalid_model_class_header_is_an_anthropic_400_without_calling_upstream()
    {
        await using var host = await StartLoggedInAsync(RouterHost.NewDataDirectory());

        using var response = await host.Client.SendAsync(Message("default", modelClass: "huge"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal("invalid_request_error", error.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Empty(host.Upstream.Requests);
    }

    [Fact]
    public async Task Errors_after_tier_resolution_carry_the_served_tier()
    {
        await using var host = await RouterHost.StartAsync();
        var client = await host.AuthorizedClientAsync();

        using var response = await client.SendAsync(Message("claude-haiku-5-5"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("low", response.Headers.GetValues(MessageRouter.ModelClassHeader).Single());
    }

    [Fact]
    public async Task New_sessions_round_robin_and_stay_pinned_across_a_restart()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        string[] sessions = ["s1", "s2", "s3"];
        var firstModels = new List<string>();
        await using (var host = await StartLoggedInAsync(dataDirectory, TwoModelMidSeed))
        {
            host.KeepData = true;
            foreach (var session in sessions)
            {
                firstModels.Add((await SendAsync(host, Message("claude-sonnet-5-5", sessionId: session))).Model);
            }
            foreach (var session in sessions.Reverse())
            {
                Assert.Equal(firstModels[Array.IndexOf(sessions, session)], (await SendAsync(host, Message("claude-sonnet-5-5", sessionId: session))).Model);
            }
        }

        Assert.Equal(["upstream-a", "upstream-b", "upstream-a"], firstModels);

        await using var restarted = await StartLoggedInAsync(dataDirectory, TwoModelMidSeed);
        foreach (var session in sessions)
        {
            Assert.Equal(firstModels[Array.IndexOf(sessions, session)], (await SendAsync(restarted, Message("claude-sonnet-5-5", sessionId: session))).Model);
        }
        Assert.Equal("upstream-b", (await SendAsync(restarted, Message("claude-sonnet-5-5", sessionId: "s4"))).Model);
    }

    [Fact]
    public async Task Same_session_id_with_different_first_user_messages_gets_separate_pins()
    {
        await using var host = await StartLoggedInAsync(RouterHost.NewDataDirectory(), TwoModelMidSeed);

        var main = (await SendAsync(host, Message("claude-sonnet-5-5", firstUser: "main task", sessionId: "shared"))).Model;
        var subAgent = (await SendAsync(host, Message("claude-sonnet-5-5", firstUser: "sub-agent task", sessionId: "shared"))).Model;
        var mainAgain = (await SendAsync(host, Message("claude-sonnet-5-5", firstUser: "main task", sessionId: "shared"))).Model;

        Assert.Equal("upstream-a", main);
        Assert.Equal("upstream-b", subAgent);
        Assert.Equal(main, mainAgain);
    }

    [Fact]
    public async Task Pins_are_per_tier_for_one_session()
    {
        await using var host = await StartLoggedInAsync(RouterHost.NewDataDirectory(), TwoModelMidSeed);

        var high = (await SendAsync(host, Message("x", sessionId: "s", modelClass: "high"))).Model;
        var mid = (await SendAsync(host, Message("claude-sonnet-5-5", sessionId: "s"))).Model;

        Assert.Equal("upstream-b", high);
        Assert.Equal("upstream-a", mid);
    }

    [Fact]
    public async Task Count_tokens_uses_the_tier_model_without_consuming_a_round_robin_turn()
    {
        await using var host = await StartLoggedInAsync(RouterHost.NewDataDirectory(), TwoModelMidSeed);

        var first = (await SendAsync(host, Message("claude-sonnet-5-5", sessionId: "s1"))).Model;
        var counted = await SendAsync(host, Message("claude-sonnet-5-5", sessionId: "s2", path: "/v1/messages/count_tokens"));
        var next = (await SendAsync(host, Message("claude-sonnet-5-5", sessionId: "s3"))).Model;

        Assert.Equal("upstream-a", first);
        Assert.Equal(("upstream-b", "mid"), counted);
        Assert.Equal("upstream-b", next);
    }

    [Fact]
    public async Task The_configured_default_tier_serves_unaliased_models()
    {
        const string lowDefaultSeed = """
            {
              "defaultTier": "low",
              "models": [
                { "id": "model-a", "provider": "claude", "upstreamModelId": "upstream-a", "enabled": true },
                { "id": "model-b", "provider": "claude", "upstreamModelId": "upstream-b", "enabled": true }
              ],
              "tiers": { "low": [ "model-a" ], "high": [ "model-b" ] },
              "aliases": []
            }
            """;
        await using var host = await StartLoggedInAsync(RouterHost.NewDataDirectory(), lowDefaultSeed);

        Assert.Equal(("upstream-a", "low"), await SendAsync(host, Message("default")));
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("""{"model":"a","model":"b","messages":[]}""")]
    [InlineData("""{"model":"a","metadata":{"user_id":"x","user_id":"y"},"messages":[]}""")]
    [InlineData("""{"model":"a","messages":[{"role":"user","content":"\udc00"}]}""")]
    public async Task Unreadable_bodies_are_a_tier_labelled_anthropic_400_that_assigns_nothing(string body)
    {
        await using var host = await StartLoggedInAsync(RouterHost.NewDataDirectory(), TwoModelMidSeed);
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(TierResolver.HeaderName, "mid");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request_error", error.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("mid", response.Headers.GetValues(MessageRouter.ModelClassHeader).Single());
        Assert.Empty(host.Upstream.Requests);
        await using var db = await Db(host);
        Assert.Empty(await db.SessionPins.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_first_requests_of_one_session_share_one_pin_and_one_turn()
    {
        await using var host = await StartLoggedInAsync(RouterHost.NewDataDirectory(), TwoModelMidSeed);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => host.Client.SendAsync(Message("claude-sonnet-5-5", sessionId: "burst"))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var models = host.Upstream.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("model").GetString()).Distinct();
        Assert.Equal(["upstream-a"], models);
        Assert.Equal("upstream-b", (await SendAsync(host, Message("claude-sonnet-5-5", sessionId: "next"))).Model);
        await using var db = await Db(host);
        Assert.Single(await db.SessionPins.Where(p => p.ClientSessionId == "burst").ToListAsync());
    }

    [Fact]
    public async Task A_session_whose_pinned_model_is_disabled_is_repinned_once()
    {
        await using var host = await StartLoggedInAsync(RouterHost.NewDataDirectory(), TwoModelMidSeed);
        Assert.Equal("upstream-a", (await SendAsync(host, Message("claude-sonnet-5-5", sessionId: "s1"))).Model);
        await using (var db = await Db(host))
        {
            (await db.Models.SingleAsync(m => m.Id == "model-a")).Enabled = false;
            await db.SaveChangesAsync();
        }

        var after = new[]
        {
            (await SendAsync(host, Message("claude-sonnet-5-5", sessionId: "s1"))).Model,
            (await SendAsync(host, Message("claude-sonnet-5-5", sessionId: "s1"))).Model,
        };

        Assert.Equal(["upstream-b", "upstream-b"], after);
        await using var check = await Db(host);
        Assert.Equal("model-b", (await check.SessionPins.SingleAsync(p => p.ClientSessionId == "s1")).ModelId);
        Assert.Equal(2, (await check.TierCursors.SingleAsync(c => c.Tier == ModelTier.Mid)).Next);
    }

    private static Task<RouterDbContext> Db(RouterHost host) =>
        host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
}
