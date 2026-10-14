using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class GatewayAuthTests
{
    public static TheoryData<string> HeaderForms => ["X-Weave-Router-Key", "x-api-key", "Authorization"];

    private static void Present(HttpRequestMessage request, string form, string key)
    {
        if (form == "Authorization")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        else
        {
            request.Headers.Add(form, key);
        }
    }

    [Theory]
    [MemberData(nameof(HeaderForms))]
    public async Task Correct_key_in_each_header_form_is_accepted(string form)
    {
        await using var host = await RouterHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        Present(request, form, await host.GatewayKeyAsync());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(HeaderForms))]
    public async Task Wrong_key_in_each_header_form_is_rejected(string form)
    {
        await using var host = await RouterHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent("{}") };
        Present(request, form, (await host.GatewayKeyAsync()) + "x");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.Upstream.Requests);
    }

    [Theory]
    [InlineData("GET", "/v1/models")]
    [InlineData("POST", "/v1/messages")]
    [InlineData("POST", "/v1/messages/count_tokens")]
    public async Task Missing_key_returns_anthropic_authentication_error(string method, string path)
    {
        await using var host = await RouterHost.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = new StringContent("{}");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("error", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("authentication_error", json.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Empty(host.Upstream.Requests);
    }

    [Fact]
    public async Task Upstream_claude_token_is_not_accepted_as_gateway_key()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync(accessToken: "access-current");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Add("x-api-key", "access-current");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
