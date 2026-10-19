using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Claude;
using SimpleRouter.Gateway;

namespace SimpleRouter.AcceptanceTests;

/// <summary>End-to-end: real router process host and real sockets, with fake Anthropic endpoints.</summary>
public class WalkingSkeletonTests
{
    [Fact]
    public async Task Gateway_client_streams_a_message_through_an_expired_subscription_login()
    {
        await using var anthropic = await FakeAnthropic.StartAsync();
        var dataDirectory = Path.Combine(Path.GetTempPath(), "simple-router-acceptance", Guid.NewGuid().ToString("N"));
        var app = RouterApp.Build([
            "--SimpleRouter:Port=0",
            $"--SimpleRouter:DataDirectory={dataDirectory}",
            $"--SimpleRouter:Claude:ApiBaseUrl={anthropic.BaseUrl}",
            $"--SimpleRouter:Claude:TokenUrl={anthropic.BaseUrl}/v1/oauth/token",
        ]);
        await RouterApp.InitializeAsync(app);
        await app.StartAsync();
        try
        {
            await app.Services.GetRequiredService<ClaudeCredentialStore>()
                .SaveAsync(new ClaudeTokens("fake-refresh-1", "stale", DateTimeOffset.UtcNow.AddHours(-1)));
            using var client = new HttpClient { BaseAddress = new UriBuilder(app.Urls.First()) { Host = "127.0.0.1" }.Uri };
            client.DefaultRequestHeaders.Add("x-api-key", await app.Services.GetRequiredService<GatewayKeyStore>().GetAsync());

            var models = await client.GetStringAsync("/v1/models?limit=1000");
            using var response = await client.PostAsync("/v1/messages",
                new StringContent("""{"model":"claude-sonnet-4-5","max_tokens":8,"stream":true,"messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"));

            Assert.Contains("\"claude-sonnet-5-5\"", models);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(FakeAnthropic.Sse, await response.Content.ReadAsStringAsync());
            Assert.Equal(1, anthropic.TokenRequests);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
