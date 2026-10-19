using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using SimpleRouter.Usage;

namespace SimpleRouter.Tests;

public class RequestOutcomeIntegrationTests
{
    private const string Session = "outcome-session";
    private const string Prompt = "synthetic-private-prompt-4829";
    private const string ToolArgument = "synthetic-private-tool-argument-5829";
    private const string UpstreamSecret = "synthetic-upstream-key-6829";
    private const string ErrorText = "synthetic-private-error-body-7829";
    private const string Partial = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}\n\n";
    private const string CompleteStream = Partial + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    private const string CompleteMessage = """
        {"id":"chatcmpl-test","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"answer"}}],"usage":{"prompt_tokens":10,"completion_tokens":3}}
        """;

    [Theory]
    [InlineData("{}", "malformed_response")]
    [InlineData("{\"error\":{\"message\":\"synthetic-private-error-body-7829\"}}", "upstream_error")]
    public async Task Invalid_local_nonstream_responses_are_failed_not_completed(string body, string kind)
    {
        await using var host = await StartAsync();
        host.Local.Respond = _ => Json(body);
        using var request = Message(false);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        AssertFailed(await UsageAsync(host), kind, streaming: false);
    }

    [Theory]
    [InlineData("", "truncated_stream")]
    [InlineData("data: [DONE]\n\n", "truncated_stream")]
    [InlineData("data: {\"choices\":[\n\n", "malformed_response")]
    [InlineData("data: {\"error\":{\"message\":\"synthetic-private-error-body-7829\"}}\n\n", "upstream_error")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"error\"}]}\n\n", "upstream_error")]
    public async Task Broken_local_streams_record_failure_even_when_http_is_200(string tail, string kind)
    {
        await using var host = await StartAsync();
        host.Local.Respond = _ => ResponsesFixtures.Stream(Partial + tail);
        using var request = Message(true);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stream = AnthropicSse.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotNull(stream.Error);
        Assert.DoesNotContain("message_stop", stream.Events);
        AssertFailed(await UsageAsync(host), kind, streaming: true);
    }

    [Fact]
    public async Task A_local_idle_timeout_is_not_misreported_as_client_cancellation()
    {
        await using var host = await StartAsync(extraArgs: ["--SimpleRouter:Local:StreamIdleTimeout=00:00:00.300"]);
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(Partial));
        host.Local.Respond = _ => Sse(pipe.Reader.AsStream());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var request = Message(true);
            using var response = await host.Client.SendAsync(request, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertFailed(await UsageAsync(host), "upstream_timeout", streaming: true);
        }
        finally
        {
            await pipe.Writer.CompleteAsync();
        }
    }

    [Theory]
    [InlineData(false, "", "truncated_stream")]
    [InlineData(true, "", "truncated_stream")]
    [InlineData(false, "data: {broken\n\n", "malformed_response")]
    [InlineData(true, "data: {broken\n\n", "malformed_response")]
    [InlineData(true, "data: {}\n\n", "malformed_response")]
    [InlineData(false, "data: {\"type\":\"response.completed\"}\n\n", "malformed_response")]
    public async Task Broken_codex_responses_record_truncation_or_malformed_failure(bool streaming, string tail, string kind)
    {
        await using var host = await StartAsync(codex: true);
        var partial = ResponsesFixtures.Sse(ResponsesFixtures.Created, ResponsesFixtures.TextDelta(0, "partial"));
        host.Codex.Respond = _ => ResponsesFixtures.Stream(partial + tail);
        using var request = Message(streaming);
        using var response = await host.Client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        if (streaming)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("message_stop", AnthropicSse.Parse(body).Events);
        }
        else
        {
            Assert.False(response.IsSuccessStatusCode);
        }
        AssertFailed(await UsageAsync(host), kind, streaming);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Successful_responses_record_completion_and_exact_pin_identity(bool codex, bool streaming)
    {
        await using var host = await StartAsync(codex);
        host.Local.Respond = _ => streaming ? ResponsesFixtures.Stream(CompleteStream) : Json(CompleteMessage);
        host.Codex.Respond = _ => ResponsesFixtures.Stream(ResponsesFixtures.PlainText);
        using var request = Message(streaming);
        using var response = await host.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        if (streaming)
            Assert.Contains("message_stop", AnthropicSse.Parse(await response.Content.ReadAsStringAsync()).Events);

        var row = await UsageAsync(host);
        Assert.Equal("completed", row.Outcome);
        Assert.Equal(streaming, row.IsStreaming);
        Assert.Null(row.FailureKind);
        Assert.Null(row.FailureDetail);
        Assert.Equal("end_turn", row.StopReason);
        Assert.Equal(Session, row.ClientSessionId);
        Assert.Equal("model-a", row.ModelId);
        Assert.Equal("upstream-a", row.UpstreamModelId);
        Assert.Equal(codex ? ProviderKind.Codex : ProviderKind.Local, row.Provider);
        Assert.Equal(SessionKey.Derive(Session, Body(streaming)).Key, row.SessionKey);
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        var pin = await db.SessionPins.SingleAsync();
        Assert.Equal(pin.SessionKey, row.SessionKey);
        Assert.Equal(pin.Tier, row.Tier);
    }

    [Fact]
    public async Task Client_cancellation_before_output_records_499_and_client_cancelled()
    {
        await using var host = await StartAsync();
        var pipe = new Pipe();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Local.Respond = _ => { entered.TrySetResult(); return Sse(pipe.Reader.AsStream()); };
        using var cancellation = new CancellationTokenSource();
        using var request = Message(true);
        try
        {
            var send = host.Client.SendAsync(request, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
            var row = await UsageAsync(host);
            Assert.Equal(499, row.Status);
            Assert.Equal("client_cancelled", row.Outcome);
            Assert.Equal("client_cancelled", row.FailureKind);
            Assert.Equal(RequestDiagnostics.Explanation("client_cancelled"), row.FailureDetail);
            Assert.True(row.IsStreaming);
            Assert.Equal("model-a", row.ModelId);
        }
        finally
        {
            await pipe.Writer.CompleteAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagnostics_do_not_persist_or_log_bodies_tool_arguments_or_credentials(bool streaming)
    {
        var logs = new CapturedLogs();
        await using var host = await StartAsync(logs: logs);
        var error = new JsonObject { ["error"] = new JsonObject { ["message"] = ErrorText + " " + UpstreamSecret } }.ToJsonString();
        host.Local.Respond = _ => streaming ? ResponsesFixtures.Stream(Partial + $"data: {error}\n\n") : Json(error);
        using var request = Message(streaming);
        using var response = await host.Client.SendAsync(request);
        var row = await UsageAsync(host);
        AssertFailed(row, "upstream_error", streaming);
        Assert.NotEmpty(logs.Messages);
        var retained = JsonSerializer.Serialize(row) + string.Join("\n", logs.Messages);
        foreach (var secret in new[] { Prompt, ToolArgument, UpstreamSecret, ErrorText, await host.GatewayKeyAsync() })
            Assert.DoesNotContain(secret, retained);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_failover_clears_the_previous_attempt_diagnostics(bool unreachable)
    {
        await using var host = await StartAsync(failover: true);
        host.Local.Respond = request => request.RequestUri!.Host == "model-a.test"
            ? unreachable ? throw new HttpRequestException(ErrorText) : Json("{}", HttpStatusCode.ServiceUnavailable)
            : Json(CompleteMessage);
        using var request = Message(false);
        using var response = await host.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var row = await UsageAsync(host);
        Assert.Equal("model-b", row.ModelId);
        Assert.Equal("upstream-b", row.UpstreamModelId);
        Assert.Equal("completed", row.Outcome);
        Assert.Null(row.FailureKind);
        Assert.Null(row.FailureDetail);
        Assert.Equal(new[] { "model-a.test", "model-b.test" }, host.Local.Requests
            .Where(r => r.Uri.AbsolutePath.EndsWith("/chat/completions")).Select(r => r.Uri.Host));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_provider_exceptions_have_safe_metadata_logs_and_matching_http_status(bool afterHeaders)
    {
        var logs = new CapturedLogs();
        await using var host = await StartAsync(logs: logs, upstream: new ThrowingUpstream(afterHeaders));
        using var request = Message(afterHeaders);
        if (afterHeaders)
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => host.Client.SendAsync(request));
        else
        {
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.DoesNotContain(ErrorText, await response.Content.ReadAsStringAsync());
        }
        var row = await UsageAsync(host);
        Assert.Equal(afterHeaders ? 200 : 502, row.Status);
        AssertFailed(row, "transport_error", afterHeaders);
        Assert.DoesNotContain(ErrorText, JsonSerializer.Serialize(row) + string.Join("\n", logs.Messages));
    }

    [Theory]
    [InlineData("data: {\"type\":\"message_stop\"}\n\n", "completed", null)]
    [InlineData("", "failed", "truncated_stream")]
    [InlineData("data: {broken\n\n", "failed", "malformed_response")]
    [InlineData("event: error\ndata: {\"type\":\"error\",\"error\":{\"message\":\"synthetic-private-error-body-7829\"}}\n\n", "failed", "upstream_error")]
    public async Task Claude_passthrough_streams_record_terminal_state_not_http200(string tail, string outcome, string? kind)
    {
        var logs = new CapturedLogs();
        await using var host = await RouterHost.StartAsync(services => services.AddLogging(builder => builder.AddProvider(logs)));
        await host.LoginAsync();
        await host.AuthorizedClientAsync();
        host.Upstream.Respond = _ => ResponsesFixtures.Stream("data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":7}}}\n\n" + tail);
        using var request = Message(true);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await UsageAsync(host);
        Assert.Equal(outcome, row.Outcome);
        Assert.Equal(kind, row.FailureKind);
        Assert.Equal(7, row.InputTokens);
        Assert.DoesNotContain(ErrorText, JsonSerializer.Serialize(row) + string.Join("\n", logs.Messages));
    }

    private sealed class ThrowingUpstream(bool afterHeaders) : IModelUpstream
    {
        public ProviderKind Provider => ProviderKind.Local;
        public async Task<UpstreamResult> SendAsync(UpstreamCall call)
        {
            if (afterHeaders)
            {
                call.Context.Response.ContentType = "text/event-stream";
                await call.Context.Response.WriteAsync("data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":7}}}\n\n");
                await call.Context.Response.Body.FlushAsync();
            }
            throw new InvalidOperationException(ErrorText);
        }
    }

    private static void AssertFailed(RequestUsage row, string kind, bool streaming)
    {
        Assert.Equal("failed", row.Outcome);
        Assert.Equal(kind, row.FailureKind);
        Assert.Equal(RequestDiagnostics.Explanation(kind), row.FailureDetail);
        Assert.False(string.IsNullOrWhiteSpace(row.FailureDetail));
        Assert.Equal(streaming, row.IsStreaming);
        Assert.Equal("model-a", row.ModelId);
        Assert.Equal("upstream-a", row.UpstreamModelId);
    }

    private static async Task<RequestUsage> UsageAsync(RouterHost host)
    {
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync();
            if (await db.RequestUsages.AsNoTracking().SingleOrDefaultAsync() is { } row) return row;
            await Task.Delay(25);
        }
        Assert.Fail("The router did not persist a request usage row within five seconds.");
        throw new InvalidOperationException();
    }

    private static JsonObject Body(bool streaming) => new()
    {
        ["model"] = "default", ["max_tokens"] = 64, ["stream"] = streaming,
        ["system"] = Prompt,
        ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "Run the test tool." },
            new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "tool_use", ["id"] = "call_test", ["name"] = "test_tool",
                    ["input"] = new JsonObject { ["value"] = ToolArgument } },
            } },
            new JsonObject { ["role"] = "user", ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "call_test", ["content"] = "ok" },
            } },
        },
        ["tools"] = new JsonArray { new JsonObject { ["name"] = "test_tool", ["description"] = ToolArgument,
            ["input_schema"] = new JsonObject { ["type"] = "object" } } },
    };

    private static HttpRequestMessage Message(bool streaming)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        { Content = new StringContent(Body(streaming).ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionKey.HeaderName, Session);
        request.Headers.Add(TierResolver.HeaderName, "high");
        return request;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static async Task<RouterHost> StartAsync(bool codex = false, bool failover = false, CapturedLogs? logs = null, IModelUpstream? upstream = null, params string[] extraArgs)
    {
        var data = RouterHost.NewDataDirectory();
        Directory.CreateDirectory(data);
        var seed = Path.Combine(data, "test-seed.json");
        await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
        {
            defaultTier = "high",
            models = new[]
            {
                new { id = "model-a", provider = codex ? "codex" : "local", upstreamModelId = "upstream-a", enabled = true, baseUrl = "http://model-a.test/v1", apiKey = UpstreamSecret },
                new { id = "model-b", provider = "local", upstreamModelId = "upstream-b", enabled = failover, baseUrl = "http://model-b.test/v1", apiKey = UpstreamSecret },
            },
            tiers = new { high = failover ? new[] { "model-a", "model-b" } : new[] { "model-a" }, mid = Array.Empty<string>(), low = Array.Empty<string>() },
            aliases = Array.Empty<object>(),
        }));
        var local = new FakeHandler { Respond = _ => Json("{}") };
        var host = await RouterHost.StartInAsync(data, local,
            services =>
            {
                if (logs is not null) services.AddLogging(builder => builder.AddProvider(logs));
                if (upstream is not null)
                {
                    services.RemoveAll<IModelUpstream>();
                    services.AddSingleton(upstream);
                }
            },
            [$"--SimpleRouter:SeedFile={seed}", .. extraArgs]);
        if (codex) await host.LoginCodexAsync();
        await host.AuthorizedClientAsync();
        return host;
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }
        private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + (exception is null ? "" : "\n" + exception));
        }
    }
}
