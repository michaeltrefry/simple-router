namespace SimpleRouter.Routing;

/// <summary>
/// Serializes a translated stream's writes to the client and, from the first write (<c>message_start</c>) until the
/// final one, sends an Anthropic <c>ping</c> whenever nothing has been written for <paramref name="interval"/>, so a client
/// that drops silent streams keeps waiting while the upstream works without output. Upstream traffic that produces no
/// client bytes does not count. A zero interval disables pings.
/// </summary>
public sealed class StreamKeepAlive(Stream body, TimeSpan interval, TimeProvider time, CancellationToken ct) : IAsyncDisposable
{
    public const string Ping = "event: ping\ndata: {\"type\": \"ping\"}\n\n";

    private static readonly ReadOnlyMemory<byte> PingBytes = System.Text.Encoding.UTF8.GetBytes(Ping);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private long _lastWrite;
    private volatile bool _ended;

    /// <summary>Writes and flushes <paramref name="bytes"/> whole; <paramref name="final"/> marks the stream's last write.</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, bool final)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await WriteLockedAsync(bytes);
            if (final)
            {
                End();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Stop the timer first so no new ping starts; the gate then waits out a ping already writing.
        End();
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            End();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteLockedAsync(ReadOnlyMemory<byte> bytes)
    {
        await body.WriteAsync(bytes, ct);
        await body.FlushAsync(ct);
        _lastWrite = time.GetTimestamp();
        if (!_ended && interval > TimeSpan.Zero)
        {
            _timer ??= time.CreateTimer(_ => _ = PingAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(interval, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task PingAsync()
    {
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (_ended)
            {
                return;
            }
            var remaining = interval - time.GetElapsedTime(_lastWrite);
            if (remaining > TimeSpan.Zero)
            {
                // A write landed while this ping waited for the gate.
                _timer?.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            await WriteLockedAsync(PingBytes);
        }
        catch (Exception)
        {
            // The client is gone; the translator's next write reports it.
            End();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void End()
    {
        _ended = true;
        _timer?.Dispose();
    }
}
