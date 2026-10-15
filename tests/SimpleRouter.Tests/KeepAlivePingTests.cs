using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.ResponsesFixtures;

namespace SimpleRouter.Tests;

/// <summary>
/// Translated (Codex and local) streams whose upstream goes quiet after <c>message_start</c> keep the client connection
/// alive with Anthropic <c>ping</c> events, and the stream still parses with the official Anthropic SDK.
/// </summary>
public class KeepAlivePingTests
{
    private const string ShortInterval = "--SimpleRouter:Limits:KeepAliveInterval=00:00:00.100";
    private const string Ping = "event: ping";
    private const string InProgress = """{"type":"response.in_progress","response":{"id":"resp_1","status":"in_progress"}}""";

    private const string LocalSeed = """
        {
          "defaultTier": "high",
          "models": [ { "id": "local-a", "provider": "local", "upstreamModelId": "upstream-local", "enabled": true, "baseUrl": "http://local-a.test/v1" } ],
          "tiers": { "high": [ "local-a" ] },
          "aliases": []
        }
        """;

    private static async Task<RouterHost> StartCodexAsync(Func<HttpRequestMessage, HttpResponseMessage> codex, params string[] args)
    {
        var host = await RouterHost.StartInAsync(RouterHost.NewDataDirectory(), args);
        await host.LoginCodexAsync();
        host.Codex.Respond = codex;
        return host;
    }

    private static async Task<RouterHost> StartLocalAsync(Func<HttpRequestMessage, HttpResponseMessage> chat, params string[] args)
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, LocalSeed);
        var local = new FakeHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/models") ? new HttpResponseMessage(HttpStatusCode.OK) : chat(request),
        };
        return await RouterHost.StartInAsync(RouterHost.NewDataDirectory(), local, [$"--SimpleRouter:SeedFile={seedPath}", .. args]);
    }

    /// <summary>An upstream body whose first bytes are sent now; the rest is written by the test through the pipe.</summary>
    private static (HttpResponseMessage Response, PipeWriter Remainder) Slow(string first)
    {
        var pipe = new Pipe();
        pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(first)).AsTask().Wait();
        var content = new StreamContent(pipe.Reader.AsStream());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return (new HttpResponseMessage(HttpStatusCode.OK) { Content = content }, pipe.Writer);
    }

    /// <summary>
    /// Keeps the upstream "alive" with frames that produce no client bytes until the client has seen a ping, then sends
    /// the rest of the stream.
    /// </summary>
    private static async Task FinishAfterPingAsync(PipeWriter upstream, string upstreamKeepAlive, Tee client, string rest)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!client.Text.Contains(Ping) && DateTime.UtcNow < until)
        {
            await upstream.WriteAsync(Encoding.UTF8.GetBytes(upstreamKeepAlive));
            await Task.Delay(20);
        }
        await upstream.WriteAsync(Encoding.UTF8.GetBytes(rest));
        await upstream.CompleteAsync();
    }

    private static async Task<AnthropicClient> SdkClientAsync(RouterHost host, Tee tee)
    {
        var http = new HttpClient(tee);
        http.DefaultRequestHeaders.Add(SessionKey.HeaderName, "keep-alive-session");
        http.DefaultRequestHeaders.Add(TierResolver.HeaderName, "high");
        return new AnthropicClient { BaseUrl = host.Client.BaseAddress!.ToString().TrimEnd('/'), ApiKey = await host.GatewayKeyAsync(), MaxRetries = 0, HttpClient = http };
    }

    private static MessageCreateParams Params() =>
        MessageCreateParams.FromRawUnchecked(
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, JsonElement>(),
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(new JsonObject
            {
                ["model"] = "claude-opus-5-5",
                ["max_tokens"] = 1024,
                ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Say hello." } },
            }.ToJsonString())!);

    private static string Text(Message message) =>
        string.Concat(message.Content.Select(b => b.TryPickText(out var text) ? text.Text : ""));

    /// <summary>The pings sit between message_start and message_stop, and the stream is otherwise a valid Anthropic stream.</summary>
    private static void AssertPingedInside(string sse)
    {
        var events = AnthropicSse.Parse(sse).Events;
        Assert.Equal("message_start", events[0]);
        Assert.Equal("message_stop", events[^1]);
        Assert.Contains("ping", events);
    }

    [Fact]
    public async Task A_codex_stream_silent_after_message_start_sends_pings_and_still_aggregates_in_the_sdk()
    {
        var (upstream, rest) = Slow(Sse(Created, ItemAdded(0, "message"), TextDelta(0, "Hel")));
        await using var host = await StartCodexAsync(_ => upstream, ShortInterval);
        using var tee = new Tee();
        var client = await SdkClientAsync(host, tee);

        var streaming = client.Messages.CreateStreaming(Params()).Aggregate();
        await FinishAfterPingAsync(rest, ": keep-alive\n\n" + Sse(InProgress), tee,
            Sse(TextDelta(0, "lo"), ItemDone(0, "message"), Completed(input: 10, output: 2)));
        var message = await streaming.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Hello", Text(message));
        Assert.Equal(StopReason.EndTurn, message.StopReason!.Value());
        AssertPingedInside(tee.Text);
    }

    [Fact]
    public async Task A_local_stream_silent_after_message_start_sends_pings_and_still_aggregates_in_the_sdk()
    {
        var (upstream, rest) = Slow("data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hel\"}}]}\n\n");
        await using var host = await StartLocalAsync(_ => upstream, ShortInterval);
        using var tee = new Tee();
        var client = await SdkClientAsync(host, tee);

        var streaming = client.Messages.CreateStreaming(Params()).Aggregate();
        await FinishAfterPingAsync(rest, ": keep-alive\n\n", tee,
            "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lo\"}}]}\n\n"
            + "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}\n\n"
            + "data: [DONE]\n\n");
        var message = await streaming.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Hello", Text(message));
        Assert.Equal(StopReason.EndTurn, message.StopReason!.Value());
        AssertPingedInside(tee.Text);
    }

    [Fact]
    public async Task A_codex_stream_without_silence_sends_no_ping()
    {
        await using var host = await StartCodexAsync(_ => Stream(ReasoningTextAndToolCall));
        using var tee = new Tee();

        await (await SdkClientAsync(host, tee)).Messages.CreateStreaming(Params()).Aggregate();

        Assert.DoesNotContain(Ping, tee.Text);
        Assert.Equal("message_stop", AnthropicSse.Parse(tee.Text).Events[^1]);
    }

    [Fact]
    public async Task A_local_stream_without_silence_sends_no_ping()
    {
        await using var host = await StartLocalAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                Encoding.UTF8, "text/event-stream"),
        });
        using var tee = new Tee();

        await (await SdkClientAsync(host, tee)).Messages.CreateStreaming(Params()).Aggregate();

        Assert.DoesNotContain(Ping, tee.Text);
        Assert.Equal("message_stop", AnthropicSse.Parse(tee.Text).Events[^1]);
    }

    [Fact]
    public async Task A_codex_stream_silent_before_message_start_sends_no_ping_while_it_holds_the_start()
    {
        var (upstream, rest) = Slow(Sse(Created));
        await using var host = await StartCodexAsync(_ => upstream, ShortInterval, "--SimpleRouter:Limits:StreamStartGrace=00:00:30");
        using var tee = new Tee();
        var client = await SdkClientAsync(host, tee);

        var streaming = client.Messages.CreateStreaming(Params()).Aggregate();
        for (var i = 0; i < 25; i++)
        {
            await rest.WriteAsync(Encoding.UTF8.GetBytes(": keep-alive\n\n" + Sse(InProgress)));
            await Task.Delay(20);
        }
        Assert.Equal("", tee.Text);
        await rest.WriteAsync(Encoding.UTF8.GetBytes(Sse(ItemAdded(0, "message"), TextDelta(0, "Hi"), ItemDone(0, "message"), Completed())));
        await rest.CompleteAsync();
        var message = await streaming.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Hi", Text(message));
        Assert.DoesNotContain(Ping, tee.Text);
    }

    [Fact]
    public async Task A_local_stream_silent_before_message_start_sends_no_ping_while_it_holds_the_start()
    {
        var (upstream, rest) = Slow(": prefill\n\n");
        await using var host = await StartLocalAsync(_ => upstream, ShortInterval, "--SimpleRouter:Limits:StreamStartGrace=00:00:30");
        using var tee = new Tee();
        var client = await SdkClientAsync(host, tee);

        var streaming = client.Messages.CreateStreaming(Params()).Aggregate();
        for (var i = 0; i < 25; i++)
        {
            await rest.WriteAsync(Encoding.UTF8.GetBytes(": prefill\n\n"));
            await Task.Delay(20);
        }
        Assert.Equal("", tee.Text);
        await rest.WriteAsync(Encoding.UTF8.GetBytes(
            "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        await rest.CompleteAsync();
        var message = await streaming.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Hi", Text(message));
        Assert.DoesNotContain(Ping, tee.Text);
    }

    /// <summary>Records the response body bytes as the SDK reads them.</summary>
    private sealed class Tee() : DelegatingHandler(new HttpClientHandler())
    {
        private readonly StringBuilder _seen = new();

        public string Text
        {
            get
            {
                lock (_seen)
                {
                    return _seen.ToString();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            var recording = new StreamContent(new Recording(await response.Content.ReadAsStreamAsync(cancellationToken), this));
            foreach (var header in response.Content.Headers)
            {
                recording.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            response.Content = recording;
            return response;
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            lock (_seen)
            {
                _seen.Append(Encoding.UTF8.GetString(bytes));
            }
        }

        private sealed class Recording(Stream inner, Tee tee) : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await inner.ReadAsync(buffer, cancellationToken);
                tee.Append(buffer.Span[..read]);
                return read;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = inner.Read(buffer, offset, count);
                tee.Append(buffer.AsSpan(offset, read));
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }
                base.Dispose(disposing);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
