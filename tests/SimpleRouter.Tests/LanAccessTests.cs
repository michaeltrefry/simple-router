using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Gateway;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

/// <summary>The opt-in LAN bind: <c>/v1</c> serves other machines with the key; the dashboard answers only this one.</summary>
public class LanAccessTests
{
    private const string LanClient = "192.168.1.50";

    private static Task<RouterHost> StartAsync(params string[] extraArgs) =>
        RouterHost.StartInAsync(RouterHost.NewDataDirectory(), new FakeHandler(), RemoteIp.Register, extraArgs);

    private static IReadOnlyList<Uri> Addresses(RouterHost host) =>
        host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(a => new Uri(a)).ToList();

    private static HttpRequestMessage FromLan(HttpMethod method, string path, string remote = LanClient)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(RemoteIp.Header, remote);
        return request;
    }

    private static HttpRequestMessage LanMessage()
    {
        var request = FromLan(HttpMethod.Post, "/v1/messages");
        request.Content = new StringContent("""{"model":"default","max_tokens":16,"messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public async Task By_default_the_router_listens_on_loopback_only()
    {
        await using var host = await StartAsync();

        var address = Assert.Single(Addresses(host));

        Assert.Equal("127.0.0.1", address.Host);
    }

    [Fact]
    public async Task The_configured_listen_address_is_the_one_bound()
    {
        await using var host = await StartAsync("--SimpleRouter:ListenAddress=::1");

        var address = Assert.Single(Addresses(host));

        Assert.Equal("[::1]", address.Host);
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("::", "::")]
    [InlineData(" 192.168.1.20 ", "192.168.1.20")]
    public void A_listen_address_resolves_to_the_ip_it_names(string configured, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), new RouterOptions { ListenAddress = configured }.ResolveListenAddress());
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("192.168.1")]
    [InlineData("")]
    public void An_invalid_listen_address_stops_startup_with_an_error_naming_the_setting(string configured)
    {
        var dataDirectory = RouterHost.NewDataDirectory();

        var error = Assert.Throws<InvalidOperationException>(() =>
            RouterApp.Build([.. RouterHost.Args(dataDirectory), $"--SimpleRouter:ListenAddress={configured}"]));

        Assert.Contains("SimpleRouter:ListenAddress", error.Message);
    }

    [Fact]
    public async Task A_lan_client_is_served_on_v1_with_the_gateway_key_and_refused_without_it()
    {
        await using var host = await StartAsync();
        await host.LoginAsync();
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"msg","type":"message","usage":{"input_tokens":1,"output_tokens":1}}""", Encoding.UTF8, "application/json"),
        };

        using var keyed = LanMessage();
        keyed.Headers.Add(GatewayAuthMiddleware.RouterKeyHeader, await host.GatewayKeyAsync());
        using var keyless = LanMessage();
        using var keyedResponse = await host.Client.SendAsync(keyed);
        using var keylessResponse = await host.Client.SendAsync(keyless);

        Assert.Equal(HttpStatusCode.OK, keyedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, keylessResponse.StatusCode);
        Assert.Single(host.Upstream.Requests);
    }

    [Theory]
    [InlineData("GET", "/ui")]
    [InlineData("GET", "/ui/settings")]
    [InlineData("GET", "/_framework/blazor.web.js")]
    [InlineData("POST", "/_blazor/negotiate?negotiateVersion=1")]
    [InlineData("GET", "/ui/dashboard.css")]
    public async Task A_lan_client_is_refused_the_dashboard_even_with_a_loopback_host_and_origin(string method, string path)
    {
        await using var host = await StartAsync();
        var port = host.Client.BaseAddress!.Port;

        using var request = FromLan(new HttpMethod(method), path);
        request.Headers.Host = $"localhost:{port}";
        request.Headers.Add("Origin", $"http://localhost:{port}");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task A_loopback_client_still_gets_the_dashboard_with_the_listen_address_shown(string remote)
    {
        await using var host = await StartAsync();

        using var request = FromLan(HttpMethod.Get, "/ui", remote);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"Listening on {host.Client.BaseAddress!.ToString().TrimEnd('/')}", await response.Content.ReadAsStringAsync());
    }
}
