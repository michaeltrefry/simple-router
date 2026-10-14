using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.ResponsesFixtures;

namespace SimpleRouter.Tests;

/// <summary>One client effort, as each seeded target receives it from the router against fake upstreams.</summary>
public class EffortRoutingTests
{
    private const string Thinking = """{"type":"enabled","budget_tokens":2048}""";

    private static HttpRequestMessage Message(string sessionId, string modelClass, string? effort = "high", string? thinking = Thinking)
    {
        var body = new JsonObject
        {
            ["model"] = "claude-opus-5-5",
            ["max_tokens"] = 4096,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Think hard." } },
        };
        if (thinking is not null)
        {
            body["thinking"] = JsonNode.Parse(thinking);
        }
        if (effort is not null)
        {
            body["output_config"] = new JsonObject { ["effort"] = effort };
        }
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionKey.HeaderName, sessionId);
        request.Headers.Add(TierResolver.HeaderName, modelClass);
        return request;
    }

    private const string QwenOverride = """
        { "models": [ { "id": "qwen-3.8-37b", "enabled": true, "baseUrl": "http://qwen.test/v1",
            "effort": { "mode": "chat_template_kwargs", "levels": [ "low", "medium" ],
                        "kwargs": { "low": { "enable_thinking": false }, "medium": { "enable_thinking": true, "depth": 2 } } } } ] }
        """;

    /// <summary>The seeded catalog with qwen-3.8-37b enabled by the local override file and given a kwargs mapping.</summary>
    private static async Task<RouterHost> StartAsync(string? dataDirectory = null, string overrides = QwenOverride, ILoggerProvider? logs = null)
    {
        dataDirectory ??= RouterHost.NewDataDirectory();
        // Inside the data directory, so the host's disposal removes it too.
        var overridePath = Path.Combine(dataDirectory, $"override-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(overridePath, overrides);
        var local = new FakeHandler
        {
            Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/models")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"c","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}]}""", Encoding.UTF8, "application/json"),
                },
        };
        var host = await RouterHost.StartInAsync(dataDirectory, local,
            services => { if (logs is not null) { services.AddLogging(b => b.AddProvider(logs)); } },
            $"--SimpleRouter:SeedOverrideFile={overridePath}");
        await host.LoginAsync();
        await host.LoginCodexAsync();
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        host.Codex.Respond = _ => Stream(ReasoningTextAndToolCall);
        await host.AuthorizedClientAsync();
        return host;
    }

    private static async Task SendAsync(RouterHost host, HttpRequestMessage request)
    {
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task High_effort_reaches_claude_unchanged_codex_as_reasoning_effort_and_a_local_model_through_its_mapping()
    {
        await using var host = await StartAsync();

        // Round-robin: the tier's first new session goes to Claude, the second to its Codex member.
        await SendAsync(host, Message("claude-session", "high"));
        await SendAsync(host, Message("codex-session", "high"));
        // Low gives new sessions to a healthy local model first.
        await SendAsync(host, Message("local-session", "low"));

        var claude = JsonNode.Parse(Assert.Single(host.Upstream.Requests).Body)!;
        Assert.Equal("""{"effort":"high"}""", claude["output_config"]!.ToJsonString());
        Assert.Equal(Thinking, claude["thinking"]!.ToJsonString());
        Assert.Null(claude["reasoning"]);

        var codex = JsonNode.Parse(Assert.Single(host.Codex.Requests).Body)!;
        Assert.Equal("gpt-6.1-sol", codex["model"]!.GetValue<string>());
        Assert.Equal("""{"summary":"auto","effort":"high"}""", codex["reasoning"]!.ToJsonString());

        var chat = JsonNode.Parse(host.Local.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/chat/completions")).Body)!;
        Assert.Equal("qwen-3.8-37b", chat["model"]!.GetValue<string>());
        // high is not listed for this model; medium is the nearest level it accepts.
        Assert.Equal("""{"enable_thinking":true,"depth":2}""", chat["chat_template_kwargs"]!.ToJsonString());
        Assert.Null(chat["reasoning_effort"]);
    }

    [Fact]
    public async Task Max_effort_reaches_gpt_6_astra_as_max_and_other_codex_models_as_xhigh()
    {
        await using var host = await StartAsync();

        await SendAsync(host, Message("claude-max", "max", "max"));
        await SendAsync(host, Message("astra", "max", "max"));
        await SendAsync(host, Message("claude-high", "high", "max"));
        await SendAsync(host, Message("sol", "high", "max"));

        Assert.Equal(
            [("gpt-6-astra", "max"), ("gpt-6.1-sol", "xhigh")],
            host.Codex.Requests.Select(r => JsonNode.Parse(r.Body)!).Select(b => (b["model"]!.GetValue<string>(), b["reasoning"]!["effort"]!.GetValue<string>())));
    }

    [Fact]
    public async Task The_recorded_effort_is_the_normalized_level_or_null_without_one()
    {
        await using var host = await StartAsync();

        // The tier's second new session goes to Codex; it says high only through its thinking budget.
        await SendAsync(host, Message("claude-explicit", "high", " HIGH "));
        await SendAsync(host, Message("codex-budget", "high", effort: null, thinking: """{"type":"enabled","budget_tokens":20000}"""));
        await SendAsync(host, Message("claude-none", "high", effort: null, thinking: null));

        Assert.Equal("high", JsonNode.Parse(Assert.Single(host.Codex.Requests).Body)!["reasoning"]!["effort"]!.GetValue<string>());
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        List<RequestUsage> rows = [];
        // Recorded once each response completes, so the last one may still be writing.
        for (var i = 0; i < 100 && (rows = await db.RequestUsages.AsNoTracking().ToListAsync()).Count < 3; i++)
        {
            await Task.Delay(20);
        }
        Assert.Equal(
            new Dictionary<string, string?> { ["claude-explicit"] = "high", ["codex-budget"] = "high", ["claude-none"] = null },
            rows.ToDictionary(r => r.ClientSessionId!, r => r.Effort));
        Assert.StartsWith("gpt-", rows.Single(r => r.ClientSessionId == "codex-budget").ModelId);
    }

    private static async Task<Dictionary<string, string?>> RecordedEffortsAsync(RouterHost host, int count)
    {
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        List<RequestUsage> rows = [];
        // Recorded once each response completes, so the last one may still be writing.
        for (var i = 0; i < 100 && (rows = await db.RequestUsages.AsNoTracking().ToListAsync()).Count < count; i++)
        {
            await Task.Delay(20);
        }
        return rows.ToDictionary(r => r.ClientSessionId!, r => r.Effort);
    }

    [Fact]
    public async Task The_recorded_effort_is_the_level_each_target_was_sent()
    {
        await using var host = await StartAsync();

        await SendAsync(host, Message("claude-max", "high", "max"));
        await SendAsync(host, Message("sol-max", "high", "max"));
        await SendAsync(host, Message("qwen-high", "low", "high"));

        Assert.Equal("xhigh", JsonNode.Parse(Assert.Single(host.Codex.Requests).Body)!["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal(
            new Dictionary<string, string?> { ["claude-max"] = "max", ["sol-max"] = "xhigh", ["qwen-high"] = "medium" },
            await RecordedEffortsAsync(host, 3));
    }

    [Fact]
    public async Task A_local_model_told_no_effort_records_none()
    {
        await using var host = await StartAsync(overrides: """
            { "models": [ { "id": "qwen-3.8-37b", "enabled": true, "baseUrl": "http://qwen.test/v1", "effort": { "mode": "none", "levels": [ "low" ] } } ] }
            """);

        await SendAsync(host, Message("qwen-high", "low", "high"));

        var chat = JsonNode.Parse(host.Local.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/chat/completions")).Body)!;
        Assert.Null(chat["reasoning_effort"]);
        Assert.Null(chat["chat_template_kwargs"]);
        Assert.Equal(new Dictionary<string, string?> { ["qwen-high"] = null }, await RecordedEffortsAsync(host, 1));
    }

    private static async Task EditAsync(RouterHost host, Func<RouterDbContext, Task> edit)
    {
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        await edit(db);
        await db.SaveChangesAsync();
    }

    private static async Task<RouterHost> RestartAsync(RouterHost previous, ILoggerProvider? logs = null)
    {
        previous.KeepData = true;
        await previous.DisposeAsync();
        return await StartAsync(previous.DataDirectory, logs: logs);
    }

    [Fact]
    public async Task A_catalog_seeded_without_effort_mappings_takes_them_from_the_seed_once_and_keeps_its_own()
    {
        var first = await StartAsync();
        await EditAsync(first, async db =>
        {
            // As a catalog seeded before effort mappings existed.
            db.Settings.Remove((await db.Settings.FindAsync(CatalogSeeder.EffortSeededSetting))!);
            (await db.Models.FindAsync("gpt-6-astra"))!.Effort = null;
            (await db.Models.FindAsync("gpt-6-luna"))!.Effort = new EffortMapping(Levels: [EffortLevel.Low]);
        });

        var second = await RestartAsync(first);
        await EditAsync(second, async db =>
        {
            var models = await db.Models.ToDictionaryAsync(m => m.Id);
            Assert.Equal(EffortLevel.Max, models["gpt-6-astra"].Effort!.Levels![^1]);
            Assert.Equal([EffortLevel.Low], models["gpt-6-luna"].Effort!.Levels);
            Assert.Null(models["gpt-6.1-sol"].Effort);
            models["gpt-6-astra"].Effort = null;
        });

        await using var third = await RestartAsync(second);
        await using var reread = await third.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        Assert.Null((await reread.Models.FindAsync("gpt-6-astra"))!.Effort);
    }

    [Fact]
    public async Task An_override_with_a_kwargs_level_missing_fails_startup()
    {
        var e = await FailsStartupAsync(dataDirectory => StartAsync(dataDirectory, overrides: """
            { "models": [ { "id": "qwen-3.8-37b", "effort": { "mode": "chat_template_kwargs", "levels": [ "none", "high" ],
                "kwargs": { "none": { "enable_thinking": false } } } } ] }
            """));
        Assert.Contains("qwen-3.8-37b", e.Message);
    }

    [Fact]
    public async Task A_mapping_cleared_after_a_fresh_seed_stays_cleared()
    {
        var first = await StartAsync();
        await EditAsync(first, async db => (await db.Models.FindAsync("gpt-6-astra"))!.Effort = null);

        await using var host = await RestartAsync(first);
        await using var reread = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        Assert.Null((await reread.Models.FindAsync("gpt-6-astra"))!.Effort);
    }

    [Fact]
    public async Task A_seed_with_a_kwargs_level_missing_fails_startup()
    {
        var seed = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "seed.json"));
        const string complete = """ "kwargs": { "none": { "enable_thinking": false }, "high": { "enable_thinking": true } }""";
        Assert.Contains(complete, seed);

        await FailsStartupAsync(async dataDirectory =>
        {
            var seedPath = Path.Combine(dataDirectory, "seed.json");
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllTextAsync(seedPath, seed.Replace(complete, """ "kwargs": { "none": { "enable_thinking": false } }"""));
            return await RouterHost.StartInAsync(dataDirectory, $"--SimpleRouter:SeedFile={seedPath}");
        });
    }

    /// <summary>Asserts startup fails, and that the failed host leaves no temp data directory behind.</summary>
    private static async Task<InvalidDataException> FailsStartupAsync(Func<string, Task<RouterHost>> start)
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => start(dataDirectory));
        Assert.False(Directory.Exists(dataDirectory));
        return e;
    }

    [Fact]
    public async Task An_unreadable_stored_mapping_is_ignored_and_named_in_one_warning_at_startup()
    {
        var first = await StartAsync();
        await EditAsync(first, db => db.Database.ExecuteSqlAsync($"UPDATE Models SET Effort = '{{\"levels\":[\"huge\"]}}' WHERE Id = 'gpt-6-astra'"));

        var logs = new CapturingLogs();
        await using var host = await RestartAsync(first, logs);
        Assert.Single(logs.Warnings, w => w.Contains("gpt-6-astra") && w.Contains("effort"));

        // The tier's first new session goes to Claude, the second to gpt-6-astra, which now has only the Codex defaults.
        await SendAsync(host, Message("claude", "max", "max"));
        await SendAsync(host, Message("astra", "max", "max"));
        Assert.Equal("""{"summary":"auto","effort":"xhigh"}""", JsonNode.Parse(Assert.Single(host.Codex.Requests).Body)!["reasoning"]!.ToJsonString());
    }

    private sealed class CapturingLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
        }
        public void Dispose() { }
    }
}
