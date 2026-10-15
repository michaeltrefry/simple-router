using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        Assert.Equal($"{RouterHost.CodexApiBase}/models?client_version=99.0.0", sent.Uri.ToString());
        Assert.Equal("Bearer codex-access-current", sent.Headers["authorization"]);
        Assert.Equal("acct-test", sent.Headers["chatgpt-account-id"]);
        Assert.Equal("codex_cli_rs", sent.Headers["originator"]);
    }

    [Fact]
    public async Task The_codex_client_version_is_configurable()
    {
        var host = await RouterHost.StartInAsync(RouterHost.NewDataDirectory(), "--SimpleRouter:Codex:ClientVersion=1.2.3");
        await using (host)
        {
            await host.LoginCodexAsync();
            host.Codex.Respond = _ => Json(CodexList("gpt-a"));

            await Discovery(host).RefreshAsync(ProviderKind.Codex);

            Assert.Equal($"{RouterHost.CodexApiBase}/models?client_version=1.2.3", Assert.Single(host.Codex.Requests).Uri.ToString());
        }
    }

    [Fact]
    public async Task A_fetched_list_missing_configured_models_names_them_in_one_warning()
    {
        await using var host = await StartAsync();
        await host.LoginCodexAsync();
        var logs = new CapturingLogger();
        var discovery = new ModelDiscovery(host.Services.GetServices<IModelListSource>(), host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>(),
            TimeProvider.System, logs);

        host.Codex.Respond = _ => Json(CodexList("codex-a"));
        await discovery.RefreshAsync(ProviderKind.Codex);
        Assert.Empty(logs.Warnings);

        host.Codex.Respond = _ => Json(CodexList("gpt-newer"));
        await discovery.RefreshAsync(ProviderKind.Codex);

        var warning = Assert.Single(logs.Warnings);
        Assert.Contains("codex-a", warning);
        Assert.Contains("SimpleRouter:Codex:ClientVersion", warning);
    }

    private sealed class CapturingLogger : ILogger<ModelDiscovery>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
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

    /// <summary>A monitor and discovery on <paramref name="time"/> over <paramref name="host"/>'s sources and database.</summary>
    private static (ModelDiscovery Discovery, ModelDiscoveryMonitor Monitor) OnClock(RouterHost host, FakeTimeProvider time, TimeSpan interval)
    {
        var discovery = new ModelDiscovery(host.Services.GetServices<IModelListSource>(), host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>(),
            time, NullLogger<ModelDiscovery>.Instance);
        var monitor = new ModelDiscoveryMonitor(discovery, Options.Create(new RouterOptions { ModelListRefreshInterval = interval }), time,
            NullLogger<ModelDiscoveryMonitor>.Instance);
        return (discovery, monitor);
    }

    [Fact]
    public async Task At_start_a_recently_fetched_list_is_skipped_until_it_turns_one_interval_old()
    {
        await using var host = await StartAsync();
        await host.LoginCodexAsync();
        host.Codex.Respond = _ => Json(CodexList("gpt-a"));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (discovery, monitor) = OnClock(host, time, TimeSpan.FromHours(6));
        await discovery.RefreshAsync(ProviderKind.Claude);
        time.Advance(TimeSpan.FromHours(5));
        int ListCalls() => host.Upstream.Requests.Count(r => r.Uri.AbsolutePath == "/v1/models");

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await EventuallyAsync(async () => (await StoredAsync(discovery, ProviderKind.Codex))?.ModelIds?.Count == 1);
            Assert.Equal(1, ListCalls());
            time.Advance(TimeSpan.FromMinutes(59));
            await Task.Delay(100);
            Assert.Equal(1, ListCalls());

            time.Advance(TimeSpan.FromMinutes(1));

            await EventuallyAsync(() => Task.FromResult(ListCalls() == 2));
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task At_start_a_list_older_than_the_interval_is_fetched()
    {
        await using var host = await StartAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (discovery, monitor) = OnClock(host, time, TimeSpan.FromHours(6));
        await discovery.RefreshAsync(ProviderKind.Claude);
        time.Advance(TimeSpan.FromHours(7));

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await EventuallyAsync(() => Task.FromResult(host.Upstream.Requests.Count(r => r.Uri.AbsolutePath == "/v1/models") == 2));
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task At_start_a_recent_failed_attempt_does_not_block_a_retry()
    {
        await using var host = await StartAsync();
        host.Upstream.Respond = Claude(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (discovery, monitor) = OnClock(host, time, TimeSpan.FromHours(6));
        await discovery.RefreshAsync(ProviderKind.Claude);
        time.Advance(TimeSpan.FromHours(1));
        host.Upstream.Respond = Claude(() => Json(ClaudeList("upstream-a", "upstream-b")));

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await EventuallyAsync(async () => (await StoredAsync(discovery, ProviderKind.Claude))?.ModelIds?.Count == 2);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A factory whose contexts run <paramref name="saving"/> before each save.</summary>
    private sealed class InterceptedFactory(IDbContextFactory<RouterDbContext> inner, Action saving) : SaveChangesInterceptor, IDbContextFactory<RouterDbContext>
    {
        public RouterDbContext CreateDbContext()
        {
            using var db = inner.CreateDbContext();
            return new RouterDbContext(new DbContextOptionsBuilder<RouterDbContext>().UseSqlite(db.Database.GetConnectionString()).AddInterceptors(this).Options);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            saving();
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    private static ModelDiscovery WithSaving(RouterHost host, Action saving) =>
        new(host.Services.GetServices<IModelListSource>(), new InterceptedFactory(host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>(), saving),
            TimeProvider.System, NullLogger<ModelDiscovery>.Instance);

    [Fact]
    public async Task A_first_row_inserted_concurrently_by_another_process_is_updated_instead()
    {
        await using var host = await StartAsync();
        var saves = 0;
        var discovery = WithSaving(host, () =>
        {
            if (saves++ == 0)
            {
                using var other = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContext();
                other.ProviderModelLists.Add(new ProviderModelList { Provider = ProviderKind.Claude, ModelIds = ["from-the-cli"], CheckedAt = DateTimeOffset.UtcNow });
                other.SaveChanges();
            }
        });

        var stored = await discovery.RefreshAsync(ProviderKind.Claude);

        Assert.Null(stored.LastError);
        Assert.Equal(["upstream-a", "upstream-b"], (await StoredAsync(host, ProviderKind.Claude))!.ModelIds);
    }

    [Fact]
    public async Task A_failed_save_after_login_is_reported_without_failing()
    {
        await using var host = await StartAsync();
        var discovery = WithSaving(host, () => throw new DbUpdateException("database is locked"));
        var output = new StringWriter();

        await discovery.ReportRefreshAsync(ProviderKind.Claude, output, CancellationToken.None);

        Assert.Contains("Could not fetch the Claude model list (database is locked)", output.ToString());
    }

    [Theory]
    [InlineData("no-last-id")]
    [InlineData("repeated-last-id")]
    [InlineData("endless")]
    public async Task A_claude_list_that_cannot_be_paged_to_its_end_is_a_failed_fetch(string paging)
    {
        await using var host = await StartAsync();
        await Discovery(host).RefreshAsync(ProviderKind.Claude);
        var page = 0;
        host.Upstream.Respond = _ => Json(new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject { ["type"] = "model", ["id"] = "upstream-a" }),
            ["has_more"] = true,
            ["last_id"] = paging switch { "no-last-id" => null, "repeated-last-id" => "same", _ => $"page-{page++}" },
        }.ToJsonString());

        var after = await Discovery(host).RefreshAsync(ProviderKind.Claude);

        Assert.Equal(["upstream-a", "upstream-b"], after.ModelIds);
        Assert.NotNull(after.LastError);
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
    [InlineData("claude-haiku-4-5-20251001", true)]
    [InlineData("Claude-Sonnet-4-5", true)]
    [InlineData("CLAUDE-HAIKU-4-5", true)]
    [InlineData("claude-sonnet-4", false)]
    [InlineData("claude-opus-4-5", false)]
    public void A_listed_dated_snapshot_serves_its_undated_alias(string configured, bool listed) =>
        Assert.Equal(listed, ModelDiscovery.Lists(["claude-sonnet-4-5-20250929", "claude-sonnet-4-5-beta", "claude-haiku-4-5"], configured));

    /// <summary>Stores unexpired Claude and Codex logins in <paramref name="dataDirectory"/>, as earlier CLI logins would.</summary>
    private static async Task StoreLoginsAsync(string dataDirectory)
    {
        var app = RouterApp.Build(RouterHost.Args(dataDirectory));
        try
        {
            await RouterApp.InitializeAsync(app);
            var expires = DateTimeOffset.UtcNow.AddHours(1);
            await app.Services.GetRequiredService<Claude.ClaudeCredentialStore>().SaveAsync(new Claude.ClaudeTokens("refresh", "access-stored", expires));
            await app.Services.GetRequiredService<Codex.CodexCredentialStore>().SaveAsync(new Codex.CodexTokens("codex-refresh", "codex-access-stored", expires, "acct"));
        }
        finally
        {
            await app.DisposeAsync();
            RouterHost.ClearPool(dataDirectory);
        }
    }

    [Fact]
    public async Task Models_refresh_fetches_every_logged_in_subscriptions_list_now()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        await StoreLoginsAsync(dataDirectory);
        var upstream = new FakeHandler { Respond = Claude(() => Json(ClaudeList("upstream-a", "upstream-b"))) };
        var codex = new FakeHandler { Respond = _ => Json(CodexList("gpt-a")) };
        var output = new StringWriter();

        var exit = await CommandLine.RunAsync(["models", "refresh", .. RouterHost.Args(dataDirectory)], TextReader.Null, output,
            RouterHost.FakeHttp(upstream, new FakeHandler(), codex: codex));

        Assert.Equal(0, exit);
        Assert.Contains("Found 2 Claude models.", output.ToString());
        Assert.Contains("Found 1 Codex models.", output.ToString());
        Assert.Equal("Bearer access-stored", Assert.Single(upstream.Requests).Headers["authorization"]);
        Assert.Equal(["upstream-a", "upstream-b"], (await StoredAsync(dataDirectory, ProviderKind.Claude))!.ModelIds);
        Assert.Equal(["gpt-a"], (await StoredAsync(dataDirectory, ProviderKind.Codex))!.ModelIds);
    }

    [Fact]
    public async Task Models_refresh_without_a_login_says_so_and_fetches_nothing()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var upstream = new FakeHandler { Respond = Claude(() => Json(ClaudeList("upstream-a"))) };
        var output = new StringWriter();

        var exit = await CommandLine.RunAsync(["models", "refresh", .. RouterHost.Args(dataDirectory)], TextReader.Null, output,
            RouterHost.FakeHttp(upstream, new FakeHandler()));

        Assert.Equal(1, exit);
        Assert.Contains("No subscription is logged in", output.ToString());
        Assert.Empty(upstream.Requests);
    }

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
            RouterHost.ClearPool(dataDirectory);
        }
    }
}
