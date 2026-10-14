namespace SimpleRouter.Routing;

/// <summary>
/// Bounds how long a translated stream may withhold <c>message_start</c>: once <paramref name="period"/> passes without
/// it, <paramref name="commit"/> sends it (and the headers), so a long reasoning phase or prefill does not leave the client
/// without a response. An error after that point can no longer fail over.
/// </summary>
public sealed class StreamStartGrace(TimeSpan period, TimeProvider time, Func<Task> commit)
{
    private readonly long _from = time.GetTimestamp();
    private bool _done;

    /// <summary>Awaits <paramref name="read"/>, committing the start first if the grace period ends while it is pending.</summary>
    public async Task<T> AwaitAsync<T>(Task<T> read, CancellationToken ct)
    {
        if (!_done)
        {
            var remaining = period - time.GetElapsedTime(_from);
            if (remaining > TimeSpan.Zero)
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var expired = Task.Delay(remaining, time, cancel.Token);
                if (await Task.WhenAny(read, expired) == read || !expired.IsCompletedSuccessfully)
                {
                    await cancel.CancelAsync();
                    return await read;
                }
            }
            _done = true;
            await commit();
        }
        return await read;
    }
}
