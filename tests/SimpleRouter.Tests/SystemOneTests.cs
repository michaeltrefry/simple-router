using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Data;
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
            { "id": "d3-off", "provider": "systemone", "upstreamModelId": "vllm-d3", "enabled": false, "baseUrl": "http://decide-off.test/v1" }
          ],
          "tiers": { "high": [ "sub-high" ] },
          "aliases": []
        }
        """;

    private const string Answer = """{"model":"vllm-d3","answers":{"kind":{"type":"choice","choice":"code","probabilities":{"code":0.99,"chat":0.01},"confidence":0.99}},"usage":{"input_tokens":213,"output_tokens":0}}""";

    private static async Task<(RouterHost Host, FakeHandler Decide)> StartAsync()
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, Seed);
        var decide = new FakeHandler { Respond = _ => Json(Answer) };
        var host = await RouterHost.StartInAsync(RouterHost.NewDataDirectory(), new FakeHandler(),
            services => services.AddHttpClient(SystemOneProxy.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => decide),
            $"--SimpleRouter:SeedFile={seedPath}");
        await host.AuthorizedClientAsync();
        return (host, decide);
    }

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
        var (host, decide) = await StartAsync();
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
        var (host, decide) = await StartAsync();
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
        var (host, decide) = await StartAsync();
        await using var _ = host;

        using var response = await host.Client.PostAsync("/v1/systemone", Decision(model));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found_error", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["type"]!.GetValue<string>());
        Assert.Empty(decide.Requests);
    }

    [Fact]
    public async Task A_body_that_is_not_a_json_object_is_400()
    {
        var (host, _) = await StartAsync();
        await using var __ = host;

        using var response = await host.Client.PostAsync("/v1/systemone", new StringContent("[1]", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unreachable_upstream_is_503()
    {
        var (host, decide) = await StartAsync();
        await using var _ = host;
        decide.Respond = _ => throw new HttpRequestException("refused");

        using var response = await host.Client.PostAsync("/v1/systemone", Decision("d3"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Requires_the_gateway_key()
    {
        var (host, decide) = await StartAsync();
        await using var _ = host;
        using var anonymous = new HttpClient { BaseAddress = host.Client.BaseAddress };

        using var response = await anonymous.PostAsync("/v1/systemone", Decision("d3"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(decide.Requests);
    }

    [Fact]
    public async Task A_messages_request_is_never_served_by_a_systemone_model_even_as_a_tier_member()
    {
        var (host, decide) = await StartAsync();
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
