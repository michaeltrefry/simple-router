using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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

    [Fact]
    public async Task Concurrent_requests_with_an_expired_token_share_one_refresh()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync(accessToken: "access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: "refresh-old");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OAuth.RespondAsync = async _ =>
        {
            await release.Task;
            return RotatedTokens();
        };
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var client = await host.AuthorizedClientAsync();

        var sends = Enumerable.Range(0, 5).Select(_ => client.SendAsync(MessagesRequest())).ToArray();
        await WaitUntilAsync(() => host.OAuth.Requests.Count > 0);
        // Give the other requests time to reach the token provider while the first refresh is held open.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        release.SetResult();
        var responses = await Task.WhenAll(sends);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(host.OAuth.Requests);
        Assert.Equal(5, host.Upstream.Requests.Count);
        Assert.All(host.Upstream.Requests, r => Assert.Equal("Bearer access-new", r.Headers["authorization"]));
    }

    [Fact]
    public async Task Refresh_is_persisted_even_when_the_client_disconnects_mid_refresh()
    {
        await using var host = await RouterHost.StartAsync(services => services.AddHttpContextAccessor());
        await host.LoginAsync(accessToken: "access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: "refresh-old");
        var accessor = host.Services.GetRequiredService<IHttpContextAccessor>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortObserved = false;
        host.OAuth.RespondAsync = async _ =>
        {
            var aborted = accessor.HttpContext!.RequestAborted;
            entered.SetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), aborted);
            }
            catch (OperationCanceledException)
            {
                abortObserved = true;
            }
            return RotatedTokens();
        };
        var client = await host.AuthorizedClientAsync();
        using var cts = new CancellationTokenSource();

        var send = client.SendAsync(MessagesRequest(), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        var store = host.Services.GetRequiredService<ClaudeCredentialStore>();
        await WaitUntilAsync(async () => (await store.GetAsync())!.RefreshToken == "refresh-new");
        Assert.True(abortObserved);
        Assert.Equal("access-new", (await store.GetAsync())!.AccessToken);
    }

    [Fact]
    public async Task Malformed_token_response_returns_an_anthropic_shaped_502()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync(accessToken: "access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        host.OAuth.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not json</html>") };
        var client = await host.AuthorizedClientAsync();

        using var response = await client.SendAsync(MessagesRequest());

        await AssertAnthropic502Async(response, "Claude token response was malformed.");
        Assert.Empty(host.Upstream.Requests);
    }

    [Fact]
    public async Task Unreachable_token_endpoint_returns_an_anthropic_shaped_502()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync(accessToken: "access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        host.OAuth.Respond = _ => throw new HttpRequestException("connection refused");
        var client = await host.AuthorizedClientAsync();

        using var response = await client.SendAsync(MessagesRequest());

        await AssertAnthropic502Async(response, "Claude token endpoint is unreachable.");
        Assert.Empty(host.Upstream.Requests);
    }

    private static HttpResponseMessage RotatedTokens() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"access_token":"access-new","refresh_token":"refresh-new","expires_in":3600}""", Encoding.UTF8, "application/json"),
    };

    private static async Task AssertAnthropic502Async(HttpResponseMessage response, string message)
    {
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("error", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("api_error", body.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(message, body.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met within 10 seconds.");
            await Task.Delay(20);
        }
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
