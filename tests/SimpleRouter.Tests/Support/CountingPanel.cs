using SimpleRouter.Dashboard;

namespace SimpleRouter.Tests.Support;

/// <summary>A live panel whose query takes a while and counts how often it runs.</summary>
public sealed class CountingPanel : LivePanel<object>
{
    private int _queries;

    public int Queries => Volatile.Read(ref _queries);

    protected override async Task<object> QueryAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _queries);
        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        return new object();
    }
}
