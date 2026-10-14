using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SimpleRouter.Cli;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

/// <summary>Subscription model lists fetched from fake upstreams, and how they gate selection.</summary>
public class ModelDiscoveryTests
{
    private const string Seed = """
        {
          "defaultTier": "mid",
          "models": [
            { "id": "model-a", "provider": "claude", "upstreamModelId": "upstream-a", "enabled": true },
            { "id": "model-b", "provider": "claude", "upstreamModelId": "upstream-b", "enabled": true },
            { "id": "model-off", "provider": "claude", "upstreamModelId": "upstream-off", "enabled": false },
            { "id": "codex-a", "provider": "codex", "upstreamModelId": "codex-a", "enabled": true },
            { "id": "local-a", "provider": "local", "upstreamModelId": "local-a", "enabled": true, "baseUrl": "http://local.test/v1" }
          ],
          "tiers": { "mid": [ "model-a", "model-b" ] },
          "aliases": []
        }
        """;

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ClaudeList(params string[] ids) =>
        new JsonObject
        {
            ["data"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["type"] = "model", ["id"] = id, ["display_name"] = id }).ToArray()),
            ["has_more"] = false,
            ["first_id"] = ids.FirstOrDefault(),
            ["last_id"] = ids.LastOrDefault(),
        }.ToJsonString();

    private static string CodexList(params string[] slugs) =>
        new JsonObject { ["models"] = new JsonArray(slugs.Select(s => (JsonNode)new JsonObject { ["slug"] = s }).ToArray()) }.ToJsonString();

    /// <summary>Claude answers its model list with <paramref name="list"/> and messages with an empty 200.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Claude(Func<HttpResponseMessage> list) =>
        request => request.RequestUri!.AbsolutePath == "/v1/models" ? list() : Json("{}");

    private static async Task<RouterHost> StartAsync(string? dataDirectory = null)
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, Seed);
        var host = await RouterHost.StartInAsync(dataDirectory ?? RouterHost.NewDataDirectory(), $"--SimpleRouter:SeedFile={seedPath}");
        await host.LoginAsync();
        host.Upstream.Respond = Claude(() => Json(ClaudeList("upstream-a", "upstream-b")));
        await host.AuthorizedClientAsync();
        return host;
    }

    private static ModelDiscovery Discovery(RouterHost host) => host.Services.GetRequiredService<ModelDiscovery>();

    private static async Task<Dictionary<string, string>> StatusesAsync(RouterHost host) =>
        (await host.Services.GetRequiredService<ModelSelector>().StatusesAsync(CancellationToken.None)).ToDictionary(s => s.ModelId, s => s.Label);

    /// <summary>Sends one message per session and returns the upstream model each was served by.</summary>
    private static async Task<List<string>> RouteAsync(RouterHost host, int sessions)
    {
        var served = new List<string>();
        for (var i = 0; i < sessions; i++)
        {
            var body = new JsonObject
            {
                ["model"] = "anything",
                ["max_tokens"] = 16,
                ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = $"hello {i}" } },
            };
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Add(SessionKey.HeaderName, $"session-{Guid.NewGuid():N}");
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var sent = JsonDocument.Parse(host.Upstream.Requests.Last(r => r.Uri.AbsolutePath == "/v1/messages").Body);
            served.Add(sent.RootElement.GetProperty("model").GetString()!);
        }
        return served;
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met within 10 s.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Claude_list_is_fetched_with_the_subscription_auth_headers_across_pages()
    {
        await using var host = await StartAsync();
        host.Upstream.Respond = request => QueryHelpers.ParseQuery(request.RequestUri!.Query).TryGetValue("after_id", out var after)
            ? Json($$"""{"data":[{"type":"model","id":"claude-late"}],"has_more":false,"last_id":"claude-late"}""")
            : Json("""{"data":[{"type":"model","id":"claude-early"}],"has_more":true,"last_id":"claude-early"}""");

        var stored = await Discovery(host).RefreshAsync(ProviderKind.Claude);

        Assert.Null(stored.LastError);
        Assert.Equal(["claude-early", "claude-late"], stored.ModelIds);
        Assert.Equal(2, host.Upstream.Requests.Count);
        var first = host.Upstream.Requests[0];
        Assert.Equal(HttpMethod.Get, first.Method);
        Assert.Equal($"{RouterHost.UpstreamBase}/v1/models?limit=1000", first.Uri.ToString());
        Assert.Equal("Bearer access-current", first.Headers["authorization"]);
        Assert.Equal("oauth-2025-04-20", first.Headers["anthropic-beta"]);
        Assert.Equal("2023-06-01", first.Headers["anthropic-version"]);
        Assert.Equal("claude-early", QueryHelpers.ParseQuery(host.Upstream.Requests[1].Uri.Query)["after_id"]);
    }

    [Fact]
    public async Task Codex_list_is_fetched_with_the_account_headers_and_client_version()
    {
        await using var host = await StartAsync();
        await host.LoginCodexAsync();
        host.Codex.Respond = _ => Json(CodexList("gpt-b", "gpt-a"));

        var stored = await Discovery(host).RefreshAsync(ProviderKind.Codex);

        Assert.Null(stored.LastError);
        Assert.Equal(["gpt-a", "gpt-b"], stored.ModelIds);
        var sent = Assert.Single(host.Codex.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"{RouterHost.CodexApiBase}/models?client_version=0.147.0", sent.Uri.ToString());
        Assert.Equal("Bearer codex-access-current", sent.Headers["authorization"]);
        Assert.Equal("acct-test", sent.Headers["chatgpt-account-id"]);
        Assert.Equal("codex_cli_rs", sent.Headers["originator"]);
    }

    [Fact]
    public async Task A_codex_401_refreshes_the_token_once_and_retries()
    {
        await using var host = await StartAsync();
        await host.LoginCodexAsync();
        host.CodexAuth.Respond = _ => Json("""{"access_token":"codex-access-new","refresh_token":"codex-refresh-new","expires_in":3600}""");
        host.Codex.Respond = request => request.Headers.Authorization!.Parameter == "codex-access-new"
            ? Json(CodexList("gpt-a"))
            : new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var stored = await Discovery(host).RefreshAsync(ProviderKind.Codex);

        Assert.Equal(["gpt-a"], stored.ModelIds);
        Assert.Equal(2, host.Codex.Requests.Count);
        Assert.Single(host.CodexAuth.Requests);
    }

    [Fact]
    public async Task Login_claude_stores_the_current_model_list()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var upstream = new FakeHandler { Respond = Claude(() => Json(ClaudeList("upstream-a", "upstream-b"))) };
        var oauth = new FakeHandler { Respond = _ => Json("""{"access_token":"access-login","refresh_token":"refresh-login","expires_in":28800}""") };
        var output = new StringWriter();
        var input = new ClaudeLoginTests.CallbackReader(() =>
        {
            var url = output.ToString().Split('\n').First(l => l.StartsWith("http"));
            return "the-code#" + QueryHelpers.ParseQuery(new Uri(url.Trim()).Query)["state"];
        });

        var exit = await CommandLine.RunAsync(["login", "claude", .. RouterHost.Args(dataDirectory)], input, output, RouterHost.FakeHttp(upstream, oauth));

        Assert.Equal(0, exit);
        Assert.Contains("Found 2 Claude models.", output.ToString());
        Assert.Equal("Bearer access-login", Assert.Single(upstream.Requests).Headers["authorization"]);
        Assert.Equal(["upstream-a", "upstream-b"], (await StoredAsync(dataDirectory, ProviderKind.Claude))!.ModelIds);
    }

    [Fact]
    public async Task Login_codex_stores_the_current_model_list_and_a_failed_fetch_does_not_fail_it()
    {
        var issuer = new FakeHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath switch
            {
                "/api/accounts/deviceauth/usercode" => Json("""{"device_auth_id":"dev-1","user_code":"ABCD-1234","interval":"0"}"""),
                "/api/accounts/deviceauth/token" => Json("""{"authorization_code":"auth-code","code_challenge":"c","code_verifier":"v"}"""),
                _ => Json($$"""{"id_token":"{{CodexLoginTests.IdToken("acct-login")}}","access_token":"codex-access-login","refresh_token":"codex-refresh-login","expires_in":3600}"""),
            },
        };
        async Task<(int Exit, string Output, string DataDirectory)> LoginAsync(FakeHandler codex)
        {
            var dataDirectory = RouterHost.NewDataDirectory();
            var output = new StringWriter();
            var exit = await CommandLine.RunAsync(["login", "codex", .. RouterHost.Args(dataDirectory)], TextReader.Null, output,
                RouterHost.FakeHttp(new FakeHandler(), new FakeHandler(), codex: codex, codexAuth: issuer));
            return (exit, output.ToString(), dataDirectory);
        }

        var ok = await LoginAsync(new FakeHandler { Respond = _ => Json(CodexList("gpt-a")) });
        var failed = await LoginAsync(new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) });

        Assert.Equal(0, ok.Exit);
        Assert.Contains("Found 1 Codex models.", ok.Output);
        Assert.Equal(["gpt-a"], (await StoredAsync(ok.DataDirectory, ProviderKind.Codex))!.ModelIds);
        Assert.Equal(0, failed.Exit);
        Assert.Contains("Codex subscription saved.", failed.Output);
        Assert.Contains("Could not fetch the Codex model list (Codex returned HTTP 502 for its model list.)", failed.Output);
        var stored = await StoredAsync(failed.DataDirectory, ProviderKind.Codex);
        Assert.Null(stored!.ModelIds);
        Assert.Equal("Codex returned HTTP 502 for its model list.", stored.LastError);
    }

    [Fact]
    public async Task The_timer_fetches_at_start_and_again_each_interval()
    {
        await using var host = await StartAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var interval = TimeSpan.FromHours(6);
        var monitor = new ModelDiscoveryMonitor(Discovery(host), Options.Create(new RouterOptions { ModelListRefreshInterval = interval }), time,
            NullLogger<ModelDiscoveryMonitor>.Instance);
        int ListCalls() => host.Upstream.Requests.Count(r => r.Uri.AbsolutePath == "/v1/models");

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await EventuallyAsync(async () => (await StoredAsync(host, ProviderKind.Claude))?.ModelIds?.Count == 2);
            host.Upstream.Respond = Claude(() => Json(ClaudeList("upstream-a")));
            time.Advance(interval - TimeSpan.FromMinutes(1));
            await Task.Delay(100);
            Assert.Equal(1, ListCalls());

            time.Advance(TimeSpan.FromMinutes(1));

            await EventuallyAsync(async () => (await StoredAsync(host, ProviderKind.Claude))?.ModelIds?.Count == 1);
            Assert.Equal(2, ListCalls());
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_timer_skips_providers_that_are_not_logged_in()
    {
        await using var host = await StartAsync();

        await Discovery(host).RefreshAllAsync();

        Assert.Single(host.Upstream.Requests);
        Assert.Empty(host.Codex.Requests);
        Assert.Null(await StoredAsync(host, ProviderKind.Codex));
    }

    [Fact]
    public async Task A_model_missing_from_its_providers_list_is_never_selected_and_reads_unknown_model()
    {
        await using var host = await StartAsync();
        host.Upstream.Respond = Claude(() => Json(ClaudeList("upstream-a")));
        await Discovery(host).RefreshAsync(ProviderKind.Claude);

        var served = await RouteAsync(host, 4);

        Assert.Equal(["upstream-a"], served.Distinct());
        var statuses = await StatusesAsync(host);
        Assert.Equal("unknown model", statuses["model-b"]);
        Assert.Equal("eligible", statuses["model-a"]);
    }

    [Fact]
    public async Task Before_any_list_is_fetched_every_configured_model_stays_selectable()
    {
        await using var host = await StartAsync();
        host.Upstream.Respond = Claude(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Discovery(host).RefreshAsync(ProviderKind.Claude);

        var served = await RouteAsync(host, 4);

        Assert.Equal(["upstream-a", "upstream-b"], served.Distinct().Order());
        Assert.Equal("eligible", (await StatusesAsync(host))["model-b"]);
    }

    [Fact]
    public async Task A_failed_fetch_keeps_the_last_good_list_and_records_a_warning()
    {
        await using var host = await StartAsync();
        host.Upstream.Respond = Claude(() => Json(ClaudeList("upstream-a")));
        var good = await Discovery(host).RefreshAsync(ProviderKind.Claude);
        host.Upstream.Respond = Claude(() => throw new HttpRequestException("connection refused"));

        var after = await Discovery(host).RefreshAsync(ProviderKind.Claude);

        Assert.Equal(["upstream-a"], after.ModelIds);
        Assert.Equal(good.FetchedAt, after.FetchedAt);
        Assert.Equal("connection refused", after.LastError);
        Assert.Equal("unknown model", (await StatusesAsync(host))["model-b"]);
        Assert.Equal(["upstream-a"], (await RouteAsync(host, 3)).Distinct());
    }

    [Theory]
    [InlineData("""{"object":"list"}""")]
    [InlineData("""{"data":[],"has_more":false}""")]
    [InlineData("not json")]
    public async Task A_malformed_or_empty_list_is_a_failed_fetch(string body)
    {
        await using var host = await StartAsync();
        await Discovery(host).RefreshAsync(ProviderKind.Claude);
        host.Upstream.Respond = Claude(() => Json(body));

        var after = await Discovery(host).RefreshAsync(ProviderKind.Claude);

        Assert.Equal(["upstream-a", "upstream-b"], after.ModelIds);
        Assert.NotNull(after.LastError);
    }

    [Fact]
    public async Task Status_reports_disabled_not_logged_in_and_unhealthy_models()
    {
        await using var host = await StartAsync();

        var statuses = await StatusesAsync(host);

        Assert.Equal("disabled", statuses["model-off"]);
        Assert.Equal("not logged in", statuses["codex-a"]);
        Assert.Equal("unhealthy", statuses["local-a"]);
        Assert.Equal("eligible", statuses["model-a"]);
    }

    [Theory]
    [InlineData("claude-sonnet-4-5", true)]
    [InlineData("claude-sonnet-4-5-20250929", true)]
    [InlineData("claude-sonnet-4", false)]
    [InlineData("claude-opus-4-5", false)]
    public void A_listed_dated_snapshot_serves_its_undated_alias(string configured, bool listed) =>
        Assert.Equal(listed, ModelDiscovery.Lists(["claude-sonnet-4-5-20250929", "claude-sonnet-4-5-beta"], configured));

    private static Task<ProviderModelList?> StoredAsync(RouterHost host, ProviderKind provider) =>
        StoredAsync(Discovery(host), provider);

    private static async Task<ProviderModelList?> StoredAsync(ModelDiscovery discovery, ProviderKind provider) =>
        (await discovery.GetAsync()).FirstOrDefault(l => l.Provider == provider);

    private static async Task<ProviderModelList?> StoredAsync(string dataDirectory, ProviderKind provider)
    {
        var app = RouterApp.Build(RouterHost.Args(dataDirectory));
        try
        {
            return await StoredAsync(app.Services.GetRequiredService<ModelDiscovery>(), provider);
        }
        finally
        {
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }
}
