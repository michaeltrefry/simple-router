using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Claude;
using SimpleRouter.Codex;
using SimpleRouter.Dashboard;
using SimpleRouter.Dashboard.Components;
using SimpleRouter.Gateway;
using SimpleRouter.Routing;

namespace SimpleRouter.Tests.Support;

/// <summary>A router with two Claude models per tier, a local endpoint fake, and the settings page rendered over its services.</summary>
public static class SettingsHarness
{
    public const string Seed = """
        {
          "defaultTier": "high",
          "models": [
            { "id": "claude-a", "provider": "claude", "upstreamModelId": "upstream-a", "enabled": true },
            { "id": "claude-b", "provider": "claude", "upstreamModelId": "upstream-b", "enabled": true },
            { "id": "codex-c", "provider": "codex", "upstreamModelId": "upstream-c", "enabled": true },
            { "id": "local-seeded", "provider": "local", "upstreamModelId": "upstream-seeded", "enabled": true,
              "baseUrl": "http://seeded.test/v1", "apiKey": "seeded-secret-key" }
          ],
          "tiers": {
            "high": [ "claude-a", "claude-b" ],
            "mid": [ "claude-a" ],
            "low": [ "codex-c" ]
          },
          "aliases": [ { "pattern": "*opus*", "tier": "high" } ],
          "prices": {
            "claude-a": { "input": 3, "output": 15, "cacheRead": 0.3, "cacheWrite": 3.75 }
          }
        }
        """;

    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    /// <summary>Local hosts whose <c>/models</c> probe answers 200; every other local host answers 503.</summary>
    public static async Task<(RouterHost Host, HashSet<string> HealthyLocals)> StartAsync()
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, Seed);
        var healthy = new HashSet<string>();
        var local = new FakeHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/models")
                ? new HttpResponseMessage(healthy.Contains(request.RequestUri.Host) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                : Json("""{"id":"c","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"hi"}}],"usage":{"prompt_tokens":5,"completion_tokens":2}}"""),
        };
        var host = await RouterHost.StartInAsync(RouterHost.NewDataDirectory(), local, $"--SimpleRouter:SeedFile={seedPath}");
        await host.LoginAsync(accessToken: "claude-access-secret", refreshToken: "claude-refresh-secret");
        host.Upstream.Respond = _ => Json("""{"id":"msg","type":"message","usage":{"input_tokens":12,"output_tokens":34}}""");
        await host.AuthorizedClientAsync();
        return (host, healthy);
    }

    public static BunitContext Context(RouterHost host)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        foreach (var type in new[]
        {
            typeof(TimeProvider), typeof(CatalogSettings), typeof(DashboardEvents), typeof(GatewayKeyStore), typeof(ClaudeOAuthClient),
            typeof(ClaudeLoginCommand), typeof(CodexOAuthClient), typeof(CodexLoginCommand), typeof(ModelDiscovery),
        })
        {
            context.Services.AddSingleton(type, host.Services.GetRequiredService(type));
        }
        return context;
    }

    public static IRenderedComponent<SettingsPage> Render(BunitContext context)
    {
        var cut = context.Render<SettingsPage>();
        cut.WaitForAssertion(() => cut.Find("#model-settings"), Wait);
        return cut;
    }

    public static Task ChangeAsync(this IRenderedComponent<SettingsPage> cut, string selector, object value) =>
        cut.Find(selector).ChangeAsync(new ChangeEventArgs { Value = value });

    public static Task ClickAsync(this IRenderedComponent<SettingsPage> cut, string selector) =>
        cut.Find(selector).ClickAsync(new MouseEventArgs());

    public static IElement Row(this IRenderedComponent<SettingsPage> cut, string section, string modelId) =>
        cut.Find($"#{section} [data-model='{modelId}']");

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpRequestMessage Message(string sessionId, string? modelClass, string model = "default")
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 64,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hello" } },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionKey.HeaderName, sessionId);
        if (modelClass is not null)
        {
            request.Headers.Add(TierResolver.HeaderName, modelClass);
        }
        return request;
    }

    /// <summary>The upstream model the Claude fake was last sent, or the local host that served the request.</summary>
    public static async Task<string> ServedByAsync(RouterHost host, HttpRequestMessage request)
    {
        var claude = host.Upstream.Requests.Count;
        var local = host.Local.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/chat/completions"));
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (host.Upstream.Requests.Count > claude)
        {
            return JsonNode.Parse(host.Upstream.Requests[^1].Body)!["model"]!.GetValue<string>();
        }
        var chats = host.Local.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/chat/completions")).ToList();
        Assert.Equal(local + 1, chats.Count);
        return chats[^1].Uri.Host;
    }
}
