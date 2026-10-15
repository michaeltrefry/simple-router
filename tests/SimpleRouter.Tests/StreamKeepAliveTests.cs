using System.Text;
using Microsoft.Extensions.Time.Testing;
using SimpleRouter.Routing;

namespace SimpleRouter.Tests;

/// <summary>The client-silence timer of a translated stream, on a fake clock so every ping is placed exactly.</summary>
public class StreamKeepAliveTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private const string Start = "event: message_start\ndata: {}\n\n";
    private const string Delta = "event: content_block_delta\ndata: {}\n\n";
    private const string Stop = "event: message_stop\ndata: {}\n\n";
    private const string Ping = StreamKeepAlive.Ping;

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static string Text(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    [Fact]
    public void The_ping_is_the_standard_anthropic_ping_event() =>
        Assert.Equal("event: ping\ndata: {\"type\": \"ping\"}\n\n", Ping);

    [Fact]
    public async Task Nothing_is_sent_before_the_first_write_however_long_the_silence()
    {
        var time = new FakeTimeProvider();
        var body = new MemoryStream();
        await using var keepAlive = new StreamKeepAlive(body, Interval, time, CancellationToken.None);

        time.Advance(Interval * 10);

        Assert.Equal("", Text(body));
    }

    [Fact]
    public async Task Client_silence_of_one_interval_after_the_start_sends_a_ping_and_keeps_pinging()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream();
        await using var keepAlive = new StreamKeepAlive(body, Interval, time, CancellationToken.None);
        await keepAlive.WriteAsync(Bytes(Start), final: false);

        time.Advance(Interval - TimeSpan.FromMilliseconds(1));
        Assert.Equal((Start, 1), (body.Text, body.Flushes));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal((Start + Ping, 2), (body.Text, body.Flushes));
        time.Advance(Interval);
        Assert.Equal((Start + Ping + Ping, 3), (body.Text, body.Flushes));
    }

    [Fact]
    public async Task Writes_closer_together_than_the_interval_send_no_ping()
    {
        var time = new FakeTimeProvider();
        var body = new MemoryStream();
        await using var keepAlive = new StreamKeepAlive(body, Interval, time, CancellationToken.None);
        await keepAlive.WriteAsync(Bytes(Start), final: false);

        for (var i = 0; i < 5; i++)
        {
            time.Advance(Interval - TimeSpan.FromSeconds(1));
            await keepAlive.WriteAsync(Bytes(Delta), final: false);
        }

        Assert.DoesNotContain("ping", Text(body));
    }

    [Fact]
    public async Task Nothing_is_sent_after_the_final_write()
    {
        var time = new FakeTimeProvider();
        var body = new MemoryStream();
        await using var keepAlive = new StreamKeepAlive(body, Interval, time, CancellationToken.None);
        await keepAlive.WriteAsync(Bytes(Start), final: false);
        await keepAlive.WriteAsync(Bytes(Stop), final: true);

        time.Advance(Interval * 10);

        Assert.Equal(Start + Stop, Text(body));
    }

    [Fact]
    public async Task Nothing_is_sent_after_disposal()
    {
        var time = new FakeTimeProvider();
        var body = new MemoryStream();
        var keepAlive = new StreamKeepAlive(body, Interval, time, CancellationToken.None);
        await keepAlive.WriteAsync(Bytes(Start), final: false);
        await keepAlive.DisposeAsync();

        time.Advance(Interval * 10);

        Assert.Equal(Start, Text(body));
    }

    [Fact]
    public async Task A_zero_interval_disables_pings()
    {
        var time = new FakeTimeProvider();
        var body = new MemoryStream();
        await using var keepAlive = new StreamKeepAlive(body, TimeSpan.Zero, time, CancellationToken.None);
        await keepAlive.WriteAsync(Bytes(Start), final: false);

        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(Start, Text(body));
    }

    [Fact]
    public async Task A_ping_due_while_a_write_is_in_progress_never_lands_inside_it()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream();
        await using var keepAlive = new StreamKeepAlive(body, Interval, time, CancellationToken.None);
        await keepAlive.WriteAsync(Bytes(Start), final: false);

        body.StallNext = true;
        var write = keepAlive.WriteAsync(Bytes(Delta), final: false);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The ping falls due while half of the event has been written.
        time.Advance(Interval);
        await Task.Delay(50);
        body.Release.SetResult();
        await write.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(Interval);

        await WaitForAsync(() => body.Text.EndsWith(Ping));
        Assert.Equal(Start + Delta + Ping, body.Text);
    }

    [Fact]
    public async Task A_failed_ping_write_stops_pinging_without_throwing()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream();
        await using var keepAlive = new StreamKeepAlive(body, Interval, time, CancellationToken.None);
        await keepAlive.WriteAsync(Bytes(Start), final: false);
        body.Broken = true;

        time.Advance(Interval);
        time.Advance(Interval);

        Assert.Equal(1, body.FailedWrites);
    }

    [Fact]
    public async Task Any_failed_ping_write_ends_the_pings_even_after_later_writes()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream();
        await using var keepAlive = new StreamKeepAlive(body, Interval, time, CancellationToken.None);
        await keepAlive.WriteAsync(Bytes(Start), final: false);
        body.Failure = new NotSupportedException("unexpected");

        time.Advance(Interval);
        body.Failure = null;
        await keepAlive.WriteAsync(Bytes(Delta), final: false);
        time.Advance(Interval * 3);

        Assert.Equal(Start + Delta, body.Text);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>Writes the first half of a stalled write, then waits for <see cref="Release"/> before the rest.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly MemoryStream _inner = new();
        public bool StallNext { get; set; }
        public bool Broken { get; set; }
        public Exception? Failure { get; set; }
        public int FailedWrites { get; private set; }
        public int Flushes { get; private set; }
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Text
        {
            get
            {
                lock (_inner)
                {
                    return Encoding.UTF8.GetString(_inner.ToArray());
                }
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Broken)
            {
                FailedWrites++;
                throw new IOException("client went away");
            }
            if (Failure is { } failure)
            {
                throw failure;
            }
            if (!StallNext)
            {
                Append(buffer);
                return;
            }
            StallNext = false;
            var half = buffer.Length / 2;
            Append(buffer[..half]);
            Stalled.SetResult();
            await Release.Task;
            Append(buffer[half..]);
        }

        private void Append(ReadOnlyMemory<byte> bytes)
        {
            lock (_inner)
            {
                _inner.Write(bytes.Span);
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return Task.CompletedTask;
        }
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
