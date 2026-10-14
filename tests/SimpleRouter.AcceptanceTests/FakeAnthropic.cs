using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace SimpleRouter.AcceptanceTests;

/// <summary>A loopback HTTP server standing in for api.anthropic.com and the Claude token endpoint.</summary>
public sealed class FakeAnthropic : IAsyncDisposable
{
    public const string AccessToken = "fake-access-token";
    public const string Sse = "event: message_start\ndata: {\"type\":\"message_start\"}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    private readonly WebApplication _app;

    private FakeAnthropic(WebApplication app) => _app = app;

    public string BaseUrl => _app.Urls.First();
    public int TokenRequests { get; private set; }

    public static async Task<FakeAnthropic> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var fake = new FakeAnthropic(app);

        app.MapPost("/v1/oauth/token", () =>
        {
            fake.TokenRequests++;
            return Results.Text($$"""{"access_token":"{{AccessToken}}","refresh_token":"fake-refresh-2","expires_in":3600}""", "application/json");
        });
        app.MapPost("/v1/messages", async (HttpContext context) =>
        {
            if (context.Request.Headers.Authorization != $"Bearer {AccessToken}")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(Sse);
        });

        await app.StartAsync();
        return fake;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
