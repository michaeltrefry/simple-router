using System.Net;
using System.Security.Cryptography;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Claude;
using SimpleRouter.Cli;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class ClaudeLoginTests
{
    private static readonly string TokenJson = """{"access_token":"access-login","refresh_token":"refresh-login","expires_in":28800}""";

    [Fact]
    public async Task Authorize_url_carries_pkce_s256_and_claude_parameters()
    {
        await using var host = await RouterHost.StartAsync();
        var login = host.Services.GetRequiredService<ClaudeOAuthClient>().StartLogin();

        var uri = new Uri(login.AuthorizeUrl);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(RouterHost.AuthorizeUrl, uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("true", query["code"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", query["client_id"]);
        Assert.Equal("https://console.anthropic.com/oauth/code/callback", query["redirect_uri"]);
        Assert.Equal("org:create_api_key user:profile user:inference", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(login.State, query["state"]);
        var expectedChallenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(login.CodeVerifier)));
        Assert.Equal(expectedChallenge, query["code_challenge"]);
    }

    [Fact]
    public async Task Login_command_exchanges_pasted_code_and_stores_tokens()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var upstream = new FakeHandler();
        var oauth = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TokenJson) } };
        var output = new StringWriter();
        var input = new CallbackReader(() =>
        {
            var url = output.ToString().Split('\n').First(l => l.StartsWith("http"));
            return "the-code#" + QueryHelpers.ParseQuery(new Uri(url.Trim()).Query)["state"];
        });

        var exit = await CommandLine.RunAsync(["login", "claude", .. RouterHost.Args(dataDirectory)], input, output, RouterHost.FakeHttp(upstream, oauth));

        Assert.Equal(0, exit);
        var exchange = Assert.Single(oauth.Requests);
        using (var body = JsonDocument.Parse(exchange.Body))
        {
            var root = body.RootElement;
            Assert.Equal("authorization_code", root.GetProperty("grant_type").GetString());
            Assert.Equal("the-code", root.GetProperty("code").GetString());
            Assert.Equal("https://console.anthropic.com/oauth/code/callback", root.GetProperty("redirect_uri").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("code_verifier").GetString()));
            Assert.False(string.IsNullOrEmpty(root.GetProperty("state").GetString()));
        }

        var app = RouterApp.Build(RouterHost.Args(dataDirectory));
        var stored = await app.Services.GetRequiredService<ClaudeCredentialStore>().GetAsync();
        Assert.Equal("refresh-login", stored!.RefreshToken);
        Assert.Equal("access-login", stored.AccessToken);
        await app.DisposeAsync();
    }

    [Theory]
    [InlineData("no-hash-here")]
    [InlineData("the-code#wrong-state")]
    [InlineData("#")]
    public async Task Malformed_or_mismatched_paste_stores_nothing(string pasted)
    {
        await using var host = await RouterHost.StartAsync();
        var command = host.Services.GetRequiredService<ClaudeLoginCommand>();
        var login = host.Services.GetRequiredService<ClaudeOAuthClient>().StartLogin();

        await Assert.ThrowsAsync<ArgumentException>(() => command.CompleteAsync(login, pasted));

        Assert.Empty(host.OAuth.Requests);
        Assert.Null(await host.Services.GetRequiredService<ClaudeCredentialStore>().GetAsync());
    }

    [Fact]
    public async Task Rejected_exchange_fails_the_command_and_stores_nothing()
    {
        await using var host = await RouterHost.StartAsync();
        host.OAuth.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest);
        var command = host.Services.GetRequiredService<ClaudeLoginCommand>();
        var output = new StringWriter();
        var input = new CallbackReader(() => "code#" + QueryHelpers.ParseQuery(new Uri(output.ToString().Split('\n')[1].Trim()).Query)["state"]);

        Assert.Equal(1, await command.RunAsync(input, output));
        Assert.Null(await host.Services.GetRequiredService<ClaudeCredentialStore>().GetAsync());
    }

    [Fact]
    public async Task Key_show_prints_the_generated_gateway_key()
    {
        await using var host = await RouterHost.StartAsync();
        var output = new StringWriter();

        var exit = await CommandLine.RunAsync(["key", "show", .. RouterHost.Args(host.DataDirectory)], TextReader.Null, output);

        Assert.Equal(0, exit);
        Assert.Equal(await host.GatewayKeyAsync(), output.ToString().Trim());
    }

    private sealed class CallbackReader(Func<string> line) : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken ct) => ValueTask.FromResult<string?>(line());
    }
}
