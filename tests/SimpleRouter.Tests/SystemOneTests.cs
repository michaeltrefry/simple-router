using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Data;
using SimpleRouter.Local;
using SimpleRouter.Routing;
using SimpleRouter.SystemOne;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class SystemOneTests
{
    private const string Seed = """
        {
          "defaultTier": "high",
          "models": [
            { "id": "sub-high", "provider": "claude", "upstreamModelId": "upstream-sub", "enabled": true },
            { "id": "d3", "provider": "systemone", "upstreamModelId": "vllm-d3", "enabled": true, "baseUrl": "http://decide.test/v1/", "apiKey": "decide-secret" },
            { "id": "d3-off", "provider": "systemone", "upstreamModelId": "vllm-d3", "enabled": false, "baseUrl": "http://decide-off.test/v1" },
            { "id": "jev", "provider": "systemone", "upstreamModelId": "~typesafe/jev-latest", "enabled": true, "baseUrl": "http://jev.test/api/v1", "decisions": "Choice, Noul, Box, Images" }
          ],
          "tiers": { "high": [ "sub-high" ] },
          "aliases": []
        }
        """;

    private const string Answer = """{"model":"vllm-d3","answers":{"kind":{"type":"choice","choice":"code","probabilities":{"code":0.99,"chat":0.01},"confidence":0.99}},"usage":{"input_tokens":213,"output_tokens":0}}""";

    /// <summary>The health probe's endpoint: answers <see cref="Status"/> and records each probe.</summary>
    private sealed class Probe
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public FakeHandler Handler { get; }

        public Probe() => Handler = new FakeHandler { Respond = _ => new HttpResponseMessage(Status) };
    }

    private static async Task<(RouterHost Host, FakeHandler Decide, Probe Probe)> StartAsync(params string[] extraArgs)
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, Seed);
        var decide = new FakeHandler { Respond = _ => Json(Answer) };
        var probe = new Probe();
        var host = await RouterHost.StartInAsync(RouterHost.NewDataDirectory(), probe.Handler,
            services => services.AddHttpClient(SystemOneProxy.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => decide),
            [$"--SimpleRouter:SeedFile={seedPath}", .. extraArgs]);
        await host.AuthorizedClientAsync();
        return (host, decide, probe);
    }

    private static Task CheckHealthAsync(RouterHost host) => host.Services.GetRequiredService<LocalHealth>().CheckAsync(CancellationToken.None);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static StringContent Decision(string? model) => new(new JsonObject
    {
        ["model"] = model,
        ["state"] = "Write a Python function that merges two sorted lists.",
        ["questions"] = new JsonObject { ["kind"] = new JsonObject { ["type"] = "choice", ["criteria"] = new JsonObject { ["code"] = "Code", ["chat"] = "Chat" } } },
    }.ToJsonString(), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Forwards_the_body_with_the_upstream_model_id_and_returns_the_answer_unchanged()
    {
        var (host, decide, _) = await StartAsync();
        await using var _ = host;

        using var response = await host.Client.PostAsync("/v1/systemone", Decision("d3"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Answer, await response.Content.ReadAsStringAsync());
        var sent = Assert.Single(decide.Requests);
        Assert.Equal("http://decide.test/v1/systemone", sent.Uri.ToString());
        Assert.Equal("Bearer decide-secret", sent.Headers["authorization"]);
        var body = JsonNode.Parse(sent.Body)!;
        Assert.Equal("vllm-d3", body["model"]!.GetValue<string>());
        Assert.Equal("Write a Python function that merges two sorted lists.", body["state"]!.GetValue<string>());
        Assert.Equal("Code", body["questions"]!["kind"]!["criteria"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_upstream_error_status_and_body_pass_through()
    {
        var (host, decide, _) = await StartAsync();
        await using var _ = host;
        decide.Respond = _ => Json("""{"error":"instructions is not allowed"}""", HttpStatusCode.BadRequest);

        using var response = await host.Client.PostAsync("/v1/systemone", Decision("d3"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"error":"instructions is not allowed"}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nope")]
    [InlineData("d3-off")]
    [InlineData("sub-high")]
    public async Task A_missing_unknown_disabled_or_non_systemone_model_is_404_without_calling_upstream(string? model)
    {
        var (host, decide, _) = await StartAsync();
        await using var _ = host;

        using var response = await host.Client.PostAsync("/v1/systemone", Decision(model));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found_error", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["type"]!.GetValue<string>());
        Assert.Empty(decide.Requests);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("""{"model":"d3","model":"d3-off","questions":{}}""")]
    public async Task A_body_that_is_not_a_json_object_or_repeats_a_key_is_400(string body)
    {
        var (host, decide, _) = await StartAsync();
        await using var __ = host;

        using var response = await host.Client.PostAsync("/v1/systemone", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request_error", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["type"]!.GetValue<string>());
        Assert.Empty(decide.Requests);
    }

    [Fact]
    public async Task An_unreachable_upstream_is_503_and_its_model_stays_unavailable_until_a_probe_succeeds()
    {
        var (host, decide, _) = await StartAsync();
        await using var _ = host;
        decide.Respond = _ => throw new HttpRequestException("refused");

        using var refused = await host.Client.PostAsync("/v1/systemone", Decision("d3"));
        decide.Respond = _ => Json(Answer);
        using var unhealthy = await host.Client.PostAsync("/v1/systemone", Decision("d3"));
        await CheckHealthAsync(host);
        using var served = await host.Client.PostAsync("/v1/systemone", Decision("d3"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(2, decide.Requests.Count);
    }

    [Fact]
    public async Task A_model_whose_probe_failed_is_503_without_calling_upstream_until_a_probe_succeeds()
    {
        var (host, decide, probe) = await StartAsync();
        await using var _ = host;

        probe.Status = HttpStatusCode.ServiceUnavailable;
        await CheckHealthAsync(host);
        using var down = await host.Client.PostAsync("/v1/systemone", Decision("d3"));
        var dashboardStatus = (await host.Services.GetRequiredService<ModelSelector>().StatusesAsync(CancellationToken.None)).Single(s => s.ModelId == "d3").Status;
        probe.Status = HttpStatusCode.OK;
        await CheckHealthAsync(host);
        using var up = await host.Client.PostAsync("/v1/systemone", Decision("d3"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal(ModelStatus.Unhealthy, dashboardStatus);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        Assert.Single(decide.Requests);
        Assert.Contains(probe.Handler.Requests, r => r.Uri.ToString() == "http://decide.test/v1/models");
        Assert.DoesNotContain(probe.Handler.Requests, r => r.Uri.Host == "decide-off.test");
    }

    [Fact]
    public async Task An_upstream_slower_than_the_timeout_is_504()
    {
        var (host, decide, _) = await StartAsync("--SimpleRouter:SystemOne:TimeoutSeconds=1");
        await using var _ = host;
        // Headers arrive at once; the body never does, and the stalled read honors the router's deadline.
        decide.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Pipe().Reader.AsStream()) };

        using var response = await host.Client.PostAsync("/v1/systemone", Decision("d3"));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("timeout_error", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_body_over_the_systemone_limit_is_413_and_one_over_the_server_wide_limit_is_served()
    {
        var (host, decide, _) = await StartAsync("--SimpleRouter:SystemOne:MaxBodyBytes=40000000");
        await using var _ = host;
        StringContent WithImage(int bytes) => new(
            "{\"model\":\"d3\",\"state\":\"look\",\"images\":[\"data:image/png;base64," + new string('A', bytes) + "\"],\"questions\":{}}",
            Encoding.UTF8, "application/json");

        using var large = await host.Client.PostAsync("/v1/systemone", WithImage(36_000_000));
        // With 100-continue the refusal arrives before the body is sent, as it would for a well-behaved client.
        using var oversized = new HttpRequestMessage(HttpMethod.Post, "/v1/systemone") { Content = WithImage(41_000_000) };
        oversized.Headers.ExpectContinue = true;
        using var tooLarge = await host.Client.SendAsync(oversized);

        Assert.Equal(HttpStatusCode.OK, large.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Single(decide.Requests);
    }

    [Fact]
    public async Task Requires_the_gateway_key()
    {
        var (host, decide, _) = await StartAsync();
        await using var _ = host;
        using var anonymous = new HttpClient { BaseAddress = host.Client.BaseAddress };

        using var response = await anonymous.PostAsync("/v1/systemone", Decision("d3"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(decide.Requests);
    }

    [Fact]
    public async Task A_messages_request_is_never_served_by_a_systemone_model_even_as_a_tier_member()
    {
        var (host, decide, _) = await StartAsync();
        await using var _ = host;
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync())
        {
            db.TierMembers.RemoveRange(db.TierMembers);
            db.TierMembers.Add(new TierMember { Tier = ModelTier.High, ModelId = "d3", Position = 0 });
            await db.SaveChangesAsync();
        }
        var body = """{"model":"claude-opus-5-5","max_tokens":8,"messages":[{"role":"user","content":"hi"}]}""";

        using var response = await host.Client.PostAsync("/v1/messages", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Empty(decide.Requests);
    }

    private static async Task<List<RequestUsage>> UsageAsync(RouterHost host)
    {
        // Recorded before the reply is written, so it is already stored when the client has the response.
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        return await db.RequestUsages.AsNoTracking().OrderBy(u => u.Id).ToListAsync();
    }

    [Fact]
    public async Task A_served_call_is_recorded_without_a_tier_with_its_input_tokens_and_client_session()
    {
        var (host, _, _) = await StartAsync();
        await using var __ = host;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/systemone") { Content = Decision("d3") };
        request.Headers.Add(SessionKey.HeaderName, "session-1");

        using var response = await host.Client.SendAsync(request);

        var row = Assert.Single(await UsageAsync(host));
        Assert.Equal(("d3", ProviderKind.SystemOne, (ModelTier?)null, 200, 213L, 0L, "session-1"),
            (row.ModelId, row.Provider, row.Tier, row.Status, row.InputTokens, row.OutputTokens, row.ClientSessionId));
    }

    [Fact]
    public async Task Refusals_of_a_named_model_are_recorded_with_their_status_and_unknown_models_are_not()
    {
        var (host, decide, _) = await StartAsync("--SimpleRouter:SystemOne:TimeoutSeconds=1");
        await using var __ = host;

        decide.Respond = _ => Json("""{"error":"bad"}""", HttpStatusCode.BadRequest);
        using var rejected = await host.Client.PostAsync("/v1/systemone", Decision("d3"));
        decide.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Pipe().Reader.AsStream()) };
        using var timedOut = await host.Client.PostAsync("/v1/systemone", Decision("d3"));
        using var unknown = await host.Client.PostAsync("/v1/systemone", Decision("nope"));

        Assert.Equal([400, 504], (await UsageAsync(host)).Select(u => u.Status));
    }

    [Fact]
    public async Task A_call_the_client_abandons_is_recorded_as_499()
    {
        var (host, decide, _) = await StartAsync();
        await using var __ = host;
        var reached = new TaskCompletionSource();
        decide.Respond = _ =>
        {
            reached.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Pipe().Reader.AsStream()) };
        };
        using var abandon = new CancellationTokenSource();

        var call = host.Client.PostAsync("/v1/systemone", Decision("d3"), abandon.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        abandon.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        List<RequestUsage> rows;
        while ((rows = await UsageAsync(host)).Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Assert.Equal(499, Assert.Single(rows).Status);
    }

    [Fact]
    public async Task Lists_enabled_systemone_models_with_their_health_and_omits_disabled_ones()
    {
        var (host, _, probe) = await StartAsync();
        await using var __ = host;

        var up = JsonNode.Parse(await host.Client.GetStringAsync("/v1/systemone/models"))!.ToJsonString();
        probe.Status = HttpStatusCode.ServiceUnavailable;
        await CheckHealthAsync(host);
        var down = JsonNode.Parse(await host.Client.GetStringAsync("/v1/systemone/models"))!.ToJsonString();

        Assert.Equal("""{"data":[{"id":"d3","healthy":true,"question_types":["choice","noul","score","box"],"images":true,"videos":true},"""
            + """{"id":"jev","healthy":true,"question_types":["choice","noul","box"],"images":true,"videos":false}]}""", up);
        Assert.Contains("""{"id":"d3","healthy":false,""", down);
    }

    public static TheoryData<string, string> Unsupported => new()
    {
        { """{"q":{"type":"score","instructions":"How formal?","criteria":["low","high"]}}""", """[]""" },
        { """{"q":{"type":"noul","instructions":"Is it red?"}}""", """["Watch this", {"type":"video_url","video_url":{"url":"data:video/mp4;base64,AAAA"}}]""" },
        { """{"q":{"type":"noul","instructions":["Same as", {"type":"video_url","video_url":{"url":"data:video/mp4;base64,AAAA"}}]}}""", """[]""" },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public async Task A_question_type_or_media_the_model_does_not_accept_is_400_without_calling_upstream(string questions, string state)
    {
        var (host, decide, _) = await StartAsync();
        await using var __ = host;
        var body = $$"""{"model":"jev","state":{{state}},"questions":{{questions}}}""";

        using var response = await host.Client.PostAsync("/v1/systemone", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("jev does not support", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["message"]!.GetValue<string>());
        Assert.Empty(decide.Requests);
        Assert.Equal(400, Assert.Single(await UsageAsync(host)).Status);
    }

    [Fact]
    public async Task Top_level_videos_are_refused_but_images_and_box_questions_reach_a_model_that_accepts_them()
    {
        var (host, decide, _) = await StartAsync();
        await using var __ = host;
        var image = new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AAAA" } };
        JsonObject Request(JsonNode? videos) => new()
        {
            ["model"] = "jev",
            ["state"] = new JsonArray("Listing photo", image.DeepClone()),
            ["questions"] = new JsonObject { ["where"] = new JsonObject { ["type"] = "box", ["instructions"] = "the product in the listing photo" } },
            ["images"] = new JsonArray("data:image/png;base64,AAAA"),
            ["videos"] = videos,
        };

        using var refused = await host.Client.PostAsync("/v1/systemone", new StringContent(Request(new JsonArray("data:video/mp4;base64,AAAA")).ToJsonString(), Encoding.UTF8, "application/json"));
        using var served = await host.Client.PostAsync("/v1/systemone", new StringContent(Request(new JsonArray()).ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("~typesafe/jev-latest", JsonNode.Parse(Assert.Single(decide.Requests).Body)!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Systemone_models_are_not_listed_in_the_anthropic_models_endpoint()
    {
        var (host, _, _) = await StartAsync();
        await using var __ = host;
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync())
        {
            db.TierAliases.Add(new TierAlias { Pattern = "*", Tier = ModelTier.High, Position = 0 });
            await db.SaveChangesAsync();
        }

        var ids = JsonNode.Parse(await host.Client.GetStringAsync("/v1/models?limit=1000"))!["data"]!.AsArray()
            .Select(m => m!["id"]!.GetValue<string>()).ToList();

        Assert.Equal(["sub-high"], ids);
    }

    [Fact]
    public async Task The_shipped_seed_has_d3_as_a_disabled_systemone_model_outside_every_tier()
    {
        await using var host = await RouterHost.StartAsync();
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();

        var d3 = await db.Models.SingleAsync(m => m.Id == "d3");

        Assert.Equal(ProviderKind.SystemOne, d3.Provider);
        Assert.False(d3.Enabled);
        Assert.False(await db.TierMembers.AnyAsync(t => t.ModelId == "d3"));
    }
}
