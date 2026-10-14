using System.Text;
using Microsoft.AspNetCore.Http;
using SimpleRouter.Usage;

namespace SimpleRouter.Tests;

public class UsageTapTests
{
    private const string Sse =
        "event: message_start\r\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":7,\"cache_creation_input_tokens\":3,\"output_tokens\":1}}}\r\n\r\n"
        + "event: content_block_delta\r\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"\\\"usage\\\"\"}}\r\n\r\n"
        + "event: message_delta\r\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":42}}";

    private static (UsageTap Tap, MemoryStream Sink) Tap(string contentType)
    {
        var context = new DefaultHttpContext();
        context.Response.ContentType = contentType;
        var sink = new MemoryStream();
        return (new UsageTap(sink, context.Response), sink);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(4096)]
    public async Task A_stream_split_anywhere_passes_through_unchanged_and_reports_the_merged_start_and_delta_usage(int chunk)
    {
        var (tap, sink) = Tap("text/event-stream; charset=utf-8");
        var bytes = Encoding.UTF8.GetBytes(Sse);

        for (var i = 0; i < bytes.Length; i += chunk)
        {
            await tap.WriteAsync(bytes.AsMemory(i, Math.Min(chunk, bytes.Length - i)));
        }

        Assert.Equal(Sse, Encoding.UTF8.GetString(sink.ToArray()));
        Assert.Equal(new TokenUsage(7, 42, 0, 3), tap.Usage());
    }

    [Fact]
    public async Task A_json_body_reports_its_top_level_usage()
    {
        var (tap, _) = Tap("application/json");
        await tap.WriteAsync(Encoding.UTF8.GetBytes("""{"content":[{"type":"text","text":"x"}],"usage":{"input_tokens":4,"output_tokens":9,"cache_read_input_tokens":11}}"""));

        Assert.Equal(new TokenUsage(4, 9, 11, 0), tap.Usage());
    }

    [Fact]
    public async Task An_error_body_reports_no_usage()
    {
        var (tap, _) = Tap("application/json");
        await tap.WriteAsync(Encoding.UTF8.GetBytes("""{"type":"error","error":{"type":"api_error","message":"no"}}"""));

        Assert.Null(tap.Usage());
    }
}
