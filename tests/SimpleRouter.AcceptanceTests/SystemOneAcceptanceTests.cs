using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SimpleRouter.Gateway;

namespace SimpleRouter.AcceptanceTests;

/// <summary>
/// End-to-end over real sockets: the shipped seed's d3, enabled and pointed at a fake decision server by the per-machine
/// override file, answers through <c>/v1/systemone</c> and the MCP <c>systemone_decide</c> tool.
/// </summary>
public class SystemOneAcceptanceTests
{
    private const string Answer = """{"model":"vllm-d3","answers":{"kind":{"type":"choice","choice":"code","probabilities":{"code":0.9997,"math":0.0002,"chat":0.0001},"confidence":0.9997}},"usage":{"input_tokens":213,"output_tokens":0}}""";

    private const string Questions = """{"kind":{"type":"choice","instructions":"What kind of work is this?","criteria":{"code":"Writing or fixing code","math":"Mathematics","chat":"Anything else"}}}""";

    [Fact]
    public async Task The_seeded_d3_answers_over_http_and_mcp()
    {
        var upstreamModels = new List<string>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var decide = builder.Build();
        decide.MapGet("/v1/models", () => Results.Text("""{"data":[{"id":"vllm-d3"}]}""", "application/json"));
        decide.MapPost("/v1/systemone", async (HttpContext context) =>
        {
            var body = await JsonNode.ParseAsync(context.Request.Body);
            lock (upstreamModels)
            {
                upstreamModels.Add(body!["model"]!.GetValue<string>());
            }
            return Results.Text(Answer, "application/json");
        });
        await decide.StartAsync();

        var dataDirectory = Path.Combine(Path.GetTempPath(), "simple-router-acceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "seed.local.json"),
            $$"""{"models":[{"id":"d3","enabled":true,"baseUrl":"{{decide.Urls.First()}}/v1","upstreamModelId":"vllm-d3"}]}""");
        var app = RouterApp.Build(["--SimpleRouter:Port=0", $"--SimpleRouter:DataDirectory={dataDirectory}"]);
        await RouterApp.InitializeAsync(app);
        await app.StartAsync();
        try
        {
            var key = await app.Services.GetRequiredService<GatewayKeyStore>().GetAsync();
            var baseUri = new UriBuilder(app.Urls.First()) { Host = "127.0.0.1" }.Uri;
            using var client = new HttpClient { BaseAddress = baseUri };
            client.DefaultRequestHeaders.Add("x-api-key", key);

            var listed = await client.GetStringAsync("/v1/systemone/models");
            using var http = await client.PostAsync("/v1/systemone", new StringContent(
                $$"""{"model":"d3","state":"Write a Python function that merges two sorted lists.","questions":{{Questions}}}""", Encoding.UTF8, "application/json"));
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(baseUri, "/v1/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
            }));
            var tool = await mcp.CallToolAsync("systemone_decide", new Dictionary<string, object?>
            {
                ["model"] = "d3",
                ["state"] = "Write a Python function that merges two sorted lists.",
                ["questions"] = JsonNode.Parse(Questions),
            });

            Assert.Equal("""{"data":[{"id":"d3","healthy":true}]}""", listed);
            Assert.Equal(HttpStatusCode.OK, http.StatusCode);
            Assert.Equal(Answer, await http.Content.ReadAsStringAsync());
            Assert.NotEqual(true, tool.IsError);
            Assert.Equal(Answer, Assert.IsType<TextContentBlock>(Assert.Single(tool.Content)).Text);
            Assert.Equal(["vllm-d3", "vllm-d3"], upstreamModels);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
