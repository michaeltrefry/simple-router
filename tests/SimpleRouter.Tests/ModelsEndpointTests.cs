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

public class ModelsEndpointTests
{
    private static readonly string[] SeededClaudeModels = ["claude-fable-5-1", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"];

    [Fact]
    public async Task Lists_the_enabled_claude_catalog_models_that_an_alias_maps_to_a_tier_in_anthropic_shape()
    {
        await using var host = await RouterHost.StartAsync();
        var client = await host.AuthorizedClientAsync();

        using var json = JsonDocument.Parse(await client.GetStringAsync("/v1/models?limit=1000"));
        var root = json.RootElement;

        var data = root.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(SeededClaudeModels, data.Select(m => m.GetProperty("id").GetString()));
        Assert.All(data, m =>
        {
            Assert.Equal("model", m.GetProperty("type").GetString());
            Assert.False(string.IsNullOrEmpty(m.GetProperty("display_name").GetString()));
            Assert.Equal(JsonValueKind.String, m.GetProperty("created_at").ValueKind);
        });
        Assert.False(root.GetProperty("has_more").GetBoolean());
        Assert.Equal("claude-fable-5-1", root.GetProperty("first_id").GetString());
        Assert.Equal("claude-haiku-5-5", root.GetProperty("last_id").GetString());
    }

    [Fact]
    public async Task Every_listed_id_sent_back_as_the_model_is_served_in_its_tier()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync();
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var client = await host.AuthorizedClientAsync();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/v1/models?limit=1000"));

        var tiers = new List<string>();
        foreach (var id in json.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!))
        {
            var body = new JsonObject
            {
                ["model"] = id,
                ["max_tokens"] = 8,
                ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = $"hi {id}" } },
            };
            using var response = await client.PostAsync("/v1/messages", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            tiers.Add(response.Headers.GetValues(MessageRouter.ModelClassHeader).Single());
        }

        Assert.Equal(["max", "high", "mid", "low"], tiers);
        Assert.Equal(SeededClaudeModels, host.Upstream.Requests.Select(r => JsonNode.Parse(r.Body)!["model"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Disabled_models_models_no_alias_maps_and_other_providers_are_not_listed()
    {
        await using var host = await RouterHost.StartAsync();
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync())
        {
            (await db.Models.FindAsync("claude-opus-5-5"))!.Enabled = false;
            db.Models.Add(new CatalogModel { Id = "codex-sonnet-lookalike", Provider = ProviderKind.Codex, UpstreamModelId = "x", Enabled = true });
            db.TierAliases.Remove(await db.TierAliases.SingleAsync(a => a.Pattern == "*haiku*"));
            await db.SaveChangesAsync();
        }
        var client = await host.AuthorizedClientAsync();

        using var json = JsonDocument.Parse(await client.GetStringAsync("/v1/models?limit=1000"));

        Assert.Equal(["claude-fable-5-1", "claude-sonnet-5-5"],
            json.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Limit_truncates_and_reports_has_more()
    {
        await using var host = await RouterHost.StartAsync();
        var client = await host.AuthorizedClientAsync();

        using var json = JsonDocument.Parse(await client.GetStringAsync("/v1/models?limit=1"));

        Assert.Equal("claude-fable-5-1", Assert.Single(json.RootElement.GetProperty("data").EnumerateArray()).GetProperty("id").GetString());
        Assert.True(json.RootElement.GetProperty("has_more").GetBoolean());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("abc")]
    public async Task Invalid_limit_is_rejected_in_anthropic_shape(string limit)
    {
        await using var host = await RouterHost.StartAsync();
        var client = await host.AuthorizedClientAsync();

        using var response = await client.GetAsync($"/v1/models?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("error", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("invalid_request_error", body.RootElement.GetProperty("error").GetProperty("type").GetString());
    }
}
