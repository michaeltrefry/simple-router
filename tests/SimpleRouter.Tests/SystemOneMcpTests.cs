using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SimpleRouter.Data;
using SimpleRouter.Gateway;
using SimpleRouter.Routing;
using SimpleRouter.SystemOne;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

/// <summary>The MCP server at <c>/v1/mcp</c>, driven by the SDK's own client over Streamable HTTP.</summary>
public class SystemOneMcpTests
{
    private const string Seed = """
        {
          "defaultTier": "high",
          "models": [
            { "id": "d3", "provider": "systemone", "upstreamModelId": "vllm-d3", "enabled": true, "baseUrl": "http://decide.test/v1" }
          ],
          "tiers": {},
          "aliases": []
        }
        """;

    private const string Answer = """{"model":"vllm-d3","answers":{"reasoning":{"type":"noul","noul":0.56}},"usage":{"input_tokens":120,"output_tokens":0}}""";

    private static async Task<RouterHost> StartAsync(params string[] extraArgs)
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, Seed);
        var local = new FakeHandler
        {
            Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/models")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer, Encoding.UTF8, "application/json") },
        };
        return await RouterHost.StartInAsync(RouterHost.NewDataDirectory(), local, [$"--SimpleRouter:SeedFile={seedPath}", .. extraArgs]);
    }

    private static async Task<McpClient> ConnectAsync(RouterHost host, string? key)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(host.Client.BaseAddress!, "/v1/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { [SessionKey.HeaderName] = "mcp-session" },
        };
        if (key is not null)
        {
            options.AdditionalHeaders[GatewayAuthMiddleware.RouterKeyHeader] = key;
        }
        return await McpClient.CreateAsync(new HttpClientTransport(options));
    }

    private static Dictionary<string, object?> Decision(string model, int imageBytes = 0)
    {
        var args = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["state"] = "Write a Python function that merges two sorted lists.",
            ["questions"] = JsonNode.Parse("""{"reasoning":{"type":"noul","instructions":"Does this need multi-step reasoning?"}}"""),
        };
        if (imageBytes > 0)
        {
            args["images"] = new[] { "data:image/png;base64," + new string('A', imageBytes) };
        }
        return args;
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task Lists_both_tools_with_the_decision_inputs_in_the_schema()
    {
        await using var host = await StartAsync();
        await using var client = await ConnectAsync(host, await host.GatewayKeyAsync());

        var tools = await client.ListToolsAsync();

        Assert.Equal(["systemone_decide", "systemone_models"], tools.Select(t => t.Name).Order());
        var properties = tools.Single(t => t.Name == "systemone_decide").JsonSchema.GetProperty("properties");
        Assert.Equal(["images", "model", "questions", "state", "videos"], properties.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public void The_schemas_do_not_depend_on_recognising_service_parameters()
    {
        // A provider that knows none of the router's services, so the SDK cannot tell a service parameter from an input:
        // the same outcome a reflection race during concurrent startup produces. The schema must not change.
        using var unknown = new ServiceCollection().BuildServiceProvider();
        var schemas = new ServiceCollection().AddMcpServer().WithTools<SystemOneTools>().Services
            .Where(d => d.ServiceType == typeof(McpServerTool))
            .Select(d => ((McpServerTool)d.ImplementationFactory!(unknown)).ProtocolTool)
            .ToDictionary(t => t.Name, t => t.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());

        Assert.Empty(schemas["systemone_models"]);
        Assert.Equal(["images", "model", "questions", "state", "videos"], schemas["systemone_decide"]);
    }

    [Fact]
    public async Task Decide_returns_the_upstream_answer_and_records_usage()
    {
        await using var host = await StartAsync();
        await using var client = await ConnectAsync(host, await host.GatewayKeyAsync());

        var models = await client.CallToolAsync("systemone_models");
        var result = await client.CallToolAsync("systemone_decide", Decision("d3"));

        Assert.Equal("""{"data":[{"id":"d3","healthy":true}]}""", Text(models));
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(Answer, Text(result));
        var sent = JsonNode.Parse(host.Local.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/systemone")).Body)!;
        Assert.Equal("vllm-d3", sent["model"]!.GetValue<string>());
        Assert.Equal("Write a Python function that merges two sorted lists.", sent["state"]!.GetValue<string>());
        Assert.Equal("noul", sent["questions"]!["reasoning"]!["type"]!.GetValue<string>());
        Assert.Null(sent["images"]);
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        var row = await db.RequestUsages.SingleAsync();
        Assert.Equal(("d3", 120L, "mcp-session"), (row.ModelId, row.InputTokens, row.ClientSessionId));
    }

    [Fact]
    public async Task Router_and_upstream_errors_come_back_as_tool_errors_with_their_message()
    {
        await using var host = await StartAsync();
        await using var client = await ConnectAsync(host, await host.GatewayKeyAsync());

        var unknown = await client.CallToolAsync("systemone_decide", Decision("nope"));
        host.Local.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/models")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"bad question"}""") };
        var rejected = await client.CallToolAsync("systemone_decide", Decision("d3"));

        Assert.True(unknown.IsError);
        Assert.Contains("No enabled SystemOne model is named 'nope'", Text(unknown));
        Assert.True(rejected.IsError);
        Assert.Equal("""HTTP 400: {"error":"bad question"}""", Text(rejected));
    }

    [Fact]
    public async Task A_decision_with_media_larger_than_the_server_wide_limit_is_accepted()
    {
        await using var host = await StartAsync();
        await using var client = await ConnectAsync(host, await host.GatewayKeyAsync());

        var result = await client.CallToolAsync("systemone_decide", Decision("d3", imageBytes: 36_000_000));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(36_000_000 + "data:image/png;base64,".Length,
            JsonNode.Parse(host.Local.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/systemone")).Body)!["images"]![0]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task A_body_over_the_systemone_limit_is_413_with_the_error_envelope()
    {
        await using var host = await StartAsync("--SimpleRouter:SystemOne:MaxBodyBytes=2000");
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "systemone_decide", ["arguments"] = new JsonObject { ["model"] = "d3", ["state"] = new string('A', 5000), ["questions"] = new JsonObject() } },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/mcp") { Content = new StringContent(call.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add(GatewayAuthMiddleware.RouterKeyHeader, await host.GatewayKeyAsync());
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        // With 100-continue the refusal arrives before the body is sent.
        request.Headers.ExpectContinue = true;

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("request_too_large", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["type"]!.GetValue<string>());
        Assert.DoesNotContain(host.Local.Requests, r => r.Uri.AbsolutePath.EndsWith("/systemone"));
    }

    [Fact]
    public async Task Requires_the_gateway_key()
    {
        await using var host = await StartAsync();

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(host, key: null));
        using var raw = await host.Client.PostAsync("/v1/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, raw.StatusCode);
        Assert.DoesNotContain(host.Local.Requests, r => r.Uri.AbsolutePath.EndsWith("/systemone"));
    }
}
