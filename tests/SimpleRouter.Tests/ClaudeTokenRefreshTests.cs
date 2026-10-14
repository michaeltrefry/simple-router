using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Claude;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class ClaudeTokenRefreshTests
{
    private static HttpRequestMessage MessagesRequest() =>
        new(HttpMethod.Post, "/v1/messages") { Content = new StringContent("""{"model":"m","max_tokens":1,"messages":[]}""", Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Expired_access_token_is_refreshed_before_the_upstream_call_and_persisted()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync(accessToken: "access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: "refresh-old");
        host.OAuth.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"access-new","refresh_token":"refresh-new","expires_in":3600}""", Encoding.UTF8, "application/json"),
        };
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var client = await host.AuthorizedClientAsync();

        using var response = await client.SendAsync(MessagesRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refresh = Assert.Single(host.OAuth.Requests);
        Assert.Equal(RouterHost.TokenUrl, refresh.Uri.ToString());
        using (var body = JsonDocument.Parse(refresh.Body))
        {
            Assert.Equal("refresh_token", body.RootElement.GetProperty("grant_type").GetString());
            Assert.Equal("refresh-old", body.RootElement.GetProperty("refresh_token").GetString());
            Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", body.RootElement.GetProperty("client_id").GetString());
        }
        Assert.Equal("Bearer access-new", Assert.Single(host.Upstream.Requests).Headers["authorization"]);

        var stored = await host.Services.GetRequiredService<ClaudeCredentialStore>().GetAsync();
        Assert.Equal("access-new", stored!.AccessToken);
        Assert.Equal("refresh-new", stored.RefreshToken);
        Assert.InRange(stored.AccessTokenExpiresAt!.Value, DateTimeOffset.UtcNow.AddMinutes(55), DateTimeOffset.UtcNow.AddMinutes(61));
        Assert.DoesNotContain("refresh-new", ReadCredentialRow(host.DataDirectory));
        Assert.DoesNotContain("access-new", ReadCredentialRow(host.DataDirectory));
    }

    [Fact]
    public async Task Refresh_response_without_a_new_refresh_token_keeps_the_old_one()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync(accessToken: "access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: "refresh-old");
        host.OAuth.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"access-new","expires_in":3600}""") };
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var client = await host.AuthorizedClientAsync();

        using var response = await client.SendAsync(MessagesRequest());

        var stored = await host.Services.GetRequiredService<ClaudeCredentialStore>().GetAsync();
        Assert.Equal("refresh-old", stored!.RefreshToken);
        Assert.Equal("access-new", stored.AccessToken);
    }

    [Fact]
    public async Task Token_close_to_expiry_is_refreshed_but_a_fresh_one_is_reused()
    {
        await using var host = await RouterHost.StartAsync();
        host.OAuth.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"access-new","refresh_token":"refresh-new","expires_in":3600}""") };
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var client = await host.AuthorizedClientAsync();

        await host.LoginAsync(accessToken: "access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(2));
        (await client.SendAsync(MessagesRequest())).Dispose();
        (await client.SendAsync(MessagesRequest())).Dispose();

        Assert.Single(host.OAuth.Requests);
        Assert.All(host.Upstream.Requests, r => Assert.Equal("Bearer access-new", r.Headers["authorization"]));
    }

    [Fact]
    public async Task Rejected_refresh_returns_502_without_calling_upstream_or_leaking_the_token_response()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync(accessToken: "access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: "refresh-old");
        host.OAuth.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant","secret":"leak-me"}""") };
        var client = await host.AuthorizedClientAsync();

        using var response = await client.SendAsync(MessagesRequest());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("leak-me", await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Upstream.Requests);
        Assert.Equal("refresh-old", (await host.Services.GetRequiredService<ClaudeCredentialStore>().GetAsync())!.RefreshToken);
    }

    private static string ReadCredentialRow(string dataDirectory)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "simple-router.db")}");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT ProtectedRefreshToken || '|' || ProtectedAccessToken FROM ClaudeCredentials";
        return (string)command.ExecuteScalar()!;
    }
}
