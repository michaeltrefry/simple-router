using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleRouter.Usage;

namespace SimpleRouter.Tests;

public class RequestCompletionTests
{
    private const string Start = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":7}}}\n\n";
    private const string Stop = "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    private static async Task<(RequestCompletion Completion, TokenUsage? Usage)> Observe(string body, bool stream = true,
        int status = 200, int chunk = 7, bool cancelled = false, Exception? exception = null, string? source = null)
    {
        var context = new DefaultHttpContext();
        context.Response.ContentType = stream ? "text/event-stream" : "application/json";
        context.Response.StatusCode = status;
        using var sink = new MemoryStream();
        var tap = new UsageTap(sink, context.Response, "test-model", NullLogger.Instance);
        var bytes = Encoding.UTF8.GetBytes(body);
        for (var i = 0; i < bytes.Length; i += chunk)
            await tap.WriteAsync(bytes.AsMemory(i, Math.Min(chunk, bytes.Length - i)));
        Assert.Equal(bytes, sink.ToArray());
        var usage = tap.Usage();
        return (tap.Completion(stream, source, cancelled, exception), usage);
    }

    [Theory]
    [InlineData("openrouter/auto")]
    [InlineData("openrouter/auto-beta")]
    public void Historical_auto_alias_metadata_is_not_presented_as_an_actual_model(string alias)
    {
        var row = new SimpleRouter.Dashboard.RequestRow(DateTimeOffset.UtcNow, null, null, "entry",
            SimpleRouter.Routing.ProviderKind.OpenRouter, null, 0, 200, 0, 0, 0, ResolvedModelId: alias);
        Assert.Null(SimpleRouter.Dashboard.DashboardFormat.ActualModel(row));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(8192)]
    public async Task Completed_stream_requires_terminal_event_and_preserves_usage(int chunk)
    {
        var (result, usage) = await Observe(Start + Stop, chunk: chunk);
        Assert.Equal("completed", result.Outcome);
        Assert.True(result.IsStreaming);
        Assert.Equal("end_turn", result.StopReason);
        Assert.Null(result.FailureKind);
        Assert.Equal(new TokenUsage(7, 3, 0, 0), usage);
    }

    [Fact]
    public async Task Http200_error_even_followed_by_stop_is_failed_and_never_retains_error_text()
    {
        var (result, _) = await Observe(Start + "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"secret-error-code\",\"message\":\"secret-prompt-token\"}}\n\n" + Stop);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("upstream_error", result.FailureKind);
        Assert.DoesNotContain("secret", result.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("event: ping\ndata: {\"type\":\"ping\"}\n\n")]
    public async Task Http200_without_message_stop_is_truncated(string tail)
    {
        var (result, _) = await Observe(Start + tail);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("truncated_stream", result.FailureKind);
    }

    [Fact]
    public async Task Cancellation_and_timeout_are_distinct_and_preserve_partial_tokens()
    {
        var (cancelled, usage) = await Observe(Start, cancelled: true, exception: new OperationCanceledException("secret"));
        var (timeout, _) = await Observe(Start, exception: new OperationCanceledException("secret"));
        Assert.Equal("client_cancelled", cancelled.Outcome);
        Assert.Equal(7, usage!.InputTokens);
        Assert.Equal("upstream_timeout", timeout.FailureKind);
        Assert.DoesNotContain("secret", cancelled + timeout.ToString());
    }

    [Fact]
    public async Task Provider_specific_category_survives_generic_translation()
    {
        var (result, _) = await Observe(Start + "event: error\ndata: {\"type\":\"error\"}\n\n", source: "upstream_timeout");
        Assert.Equal("upstream_timeout", result.FailureKind);
    }

    [Fact]
    public async Task Nonstream_message_and_error_are_not_confused_by_http200()
    {
        var (success, _) = await Observe("{\"type\":\"message\",\"content\":[],\"stop_reason\":\"tool_use\",\"usage\":{\"output_tokens\":2}}", stream: false);
        var (failure, _) = await Observe("{\"type\":\"error\",\"error\":{\"message\":\"secret\"}}", stream: false);
        Assert.Equal("completed", success.Outcome);
        Assert.False(success.IsStreaming);
        Assert.Equal("tool_use", success.StopReason);
        Assert.Equal("failed", failure.Outcome);
        Assert.DoesNotContain("secret", failure.ToString());
    }

    [Fact]
    public async Task Stop_reason_is_allowlisted_not_arbitrary_upstream_text()
    {
        var (result, _) = await Observe("{\"type\":\"message\",\"content\":[],\"stop_reason\":\"secret-prompt\"}", stream: false);
        Assert.Null(result.StopReason);
        Assert.DoesNotContain("secret", result.ToString());
    }

    [Fact]
    public async Task Multiline_event_data_and_crlf_are_supported()
    {
        var body = Start + "event: message_delta\r\ndata: {\"type\":\"message_delta\",\r\ndata: \"usage\":{\"output_tokens\":9}}\r\n\r\n" + Stop;
        var (result, usage) = await Observe(body);
        Assert.Equal("completed", result.Outcome);
        Assert.Equal(3, usage!.OutputTokens);
    }

    [Fact]
    public async Task Oversized_body_remains_unknown_instead_of_http_success_guess()
    {
        var (result, usage) = await Observe("{\"type\":\"message\",\"content\":[\"" + new string('x', 9 * 1024 * 1024) + "\"]}", stream: false, chunk: 65536);
        Assert.Equal("unknown", result.Outcome);
        Assert.Null(usage);
    }
}
