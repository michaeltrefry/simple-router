using Microsoft.AspNetCore.Components;

namespace SimpleRouter.Dashboard;

/// <summary>
/// A dashboard panel that reloads its data whenever <see cref="DashboardEvents"/> fires, and on a slow timer for changes
/// no event announces (cooldowns running out, a login made by the CLI process).
/// </summary>
public abstract class LivePanel<T> : ComponentBase, IDisposable where T : class
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _disposed = new();
    private ITimer? _timer;
    private long _version;
    private int _reloadRequested;
    private int _reloading;

    [Inject] private DashboardEvents Events { get; set; } = default!;
    [Inject] private TimeProvider Time { get; set; } = default!;
    [Inject] private ILogger<LivePanel<T>> Logger { get; set; } = default!;

    protected T? Data { get; private set; }

    protected abstract Task<T> QueryAsync(CancellationToken ct);

    protected override async Task OnInitializedAsync()
    {
        Events.Changed += OnChanged;
        _timer = Time.CreateTimer(_ => OnChanged(), null, RefreshInterval, RefreshInterval);
        await LoadAsync();
    }

    /// <summary>Reloads now, for a change made in the panel itself (a different range picked, say).</summary>
    protected void Refresh() => OnChanged();

    // Coalesces signals: at most one reload runs, and any signals arriving meanwhile cause one more.
    private void OnChanged()
    {
        Interlocked.Exchange(ref _reloadRequested, 1);
        if (Interlocked.CompareExchange(ref _reloading, 1, 0) == 0)
        {
            _ = ReloadAsync();
        }
    }

    private async Task ReloadAsync()
    {
        do
        {
            try
            {
                while (Interlocked.Exchange(ref _reloadRequested, 0) == 1 && !_disposed.IsCancellationRequested)
                {
                    await InvokeAsync(async () =>
                    {
                        await LoadAsync();
                        StateHasChanged();
                    });
                }
            }
            catch (Exception)
            {
                // The circuit went away mid-reload; there is nothing left to render into.
            }
            finally
            {
                Volatile.Write(ref _reloading, 0);
            }
        }
        while (Volatile.Read(ref _reloadRequested) == 1 && !_disposed.IsCancellationRequested
            && Interlocked.CompareExchange(ref _reloading, 1, 0) == 0);
    }

    private async Task LoadAsync()
    {
        // Loads can overlap; only the newest one may replace what is shown.
        var version = Interlocked.Increment(ref _version);
        try
        {
            var data = await QueryAsync(_disposed.Token);
            if (version == Interlocked.Read(ref _version))
            {
                Data = data;
            }
        }
        catch (Exception e)
        {
            // A failed refresh keeps the last data; it must not tear down the dashboard.
            if (!_disposed.IsCancellationRequested)
            {
                Logger.LogWarning("Could not refresh a dashboard panel: {error}", e.GetType().Name);
            }
        }
    }

    public void Dispose()
    {
        Events.Changed -= OnChanged;
        _timer?.Dispose();
        // Not disposed: a timer tick already in flight may still read its token.
        _disposed.Cancel();
        GC.SuppressFinalize(this);
    }
}
