using System.Net;
using System.Text.Json;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class ModelsEndpointTests
{
    [Fact]
    public async Task Lists_configured_models_in_anthropic_shape()
    {
        await using var host = await RouterHost.StartAsync("model-a", "model-b");
        var client = await host.AuthorizedClientAsync();

        using var json = JsonDocument.Parse(await client.GetStringAsync("/v1/models?limit=1000"));
        var root = json.RootElement;

        var data = root.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["model-a", "model-b"], data.Select(m => m.GetProperty("id").GetString()));
        Assert.All(data, m =>
        {
            Assert.Equal("model", m.GetProperty("type").GetString());
            Assert.False(string.IsNullOrEmpty(m.GetProperty("display_name").GetString()));
            Assert.Equal(JsonValueKind.String, m.GetProperty("created_at").ValueKind);
        });
        Assert.False(root.GetProperty("has_more").GetBoolean());
        Assert.Equal("model-a", root.GetProperty("first_id").GetString());
        Assert.Equal("model-b", root.GetProperty("last_id").GetString());
    }

    [Fact]
    public async Task Defaults_to_the_fixed_model_when_none_configured()
    {
        await using var host = await RouterHost.StartAsync();
        var client = await host.AuthorizedClientAsync();

        using var json = JsonDocument.Parse(await client.GetStringAsync("/v1/models?limit=1000"));

        var ids = json.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString());
        Assert.Equal(RouterOptions.DefaultModels, ids);
    }

    [Fact]
    public async Task Limit_truncates_and_reports_has_more()
    {
        await using var host = await RouterHost.StartAsync("model-a", "model-b");
        var client = await host.AuthorizedClientAsync();

        using var json = JsonDocument.Parse(await client.GetStringAsync("/v1/models?limit=1"));

        Assert.Single(json.RootElement.GetProperty("data").EnumerateArray());
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
