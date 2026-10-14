using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleRouter.Data;

namespace SimpleRouter.Routing;

/// <summary>
/// Fetches and stores each subscription's model list. A failed fetch keeps the last good list and records the error;
/// selection only excludes models once a list has been fetched successfully.
/// </summary>
public sealed class ModelDiscovery(
    IEnumerable<IModelListSource> sources,
    IDbContextFactory<RouterDbContext> dbFactory,
    TimeProvider time,
    ILogger<ModelDiscovery> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Fetches <paramref name="provider"/>'s list now and returns what is stored afterwards; never throws for a failed fetch.</summary>
    public async Task<ProviderModelList> RefreshAsync(ProviderKind provider, CancellationToken ct = default)
    {
        var source = sources.FirstOrDefault(s => s.Provider == provider)
            ?? throw new ArgumentException($"{provider} has no model list.", nameof(provider));
        await _gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<string>? ids = null;
            string? error = null;
            try
            {
                ids = await source.FetchAsync(ct);
                if (ids.Count == 0)
                {
                    error = $"{provider} returned an empty model list.";
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                error = $"The {provider} model list request timed out.";
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                error = e.Message;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.ProviderModelLists.FindAsync([provider], ct);
            if (row is null)
            {
                row = new ProviderModelList { Provider = provider };
                db.ProviderModelLists.Add(row);
            }
            var now = time.GetUtcNow();
            row.CheckedAt = now;
            row.LastError = error;
            if (error is null)
            {
                row.ModelIds = ids!.Distinct().Order(StringComparer.Ordinal).ToList();
                row.FetchedAt = now;
                logger.LogInformation("Fetched the {provider} model list: {count} models", provider, row.ModelIds.Count);
            }
            else
            {
                logger.LogWarning("Could not fetch the {provider} model list; keeping the last good list: {error}", provider, error);
            }
            await db.SaveChangesAsync(ct);
            return row;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fetches right after a login and tells the user the outcome; a failed fetch does not fail the login.</summary>
    public async Task ReportRefreshAsync(ProviderKind provider, TextWriter output, CancellationToken ct)
    {
        var list = await RefreshAsync(provider, ct);
        await output.WriteLineAsync(list.LastError is null
            ? $"Found {list.ModelIds!.Count} {provider} models."
            : $"Could not fetch the {provider} model list ({list.LastError}); the running router retries it.");
    }

    /// <summary>Refreshes every logged-in subscription, skipping those checked within <paramref name="olderThan"/>.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default, TimeSpan? olderThan = null)
    {
        List<ProviderModelList> stored;
        Func<ProviderKind, bool> loggedIn;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            stored = await db.ProviderModelLists.AsNoTracking().ToListAsync(ct);
            loggedIn = await ModelSelector.LoggedInAsync(db, ct);
        }
        var now = time.GetUtcNow();
        foreach (var provider in sources.Select(s => s.Provider).Where(loggedIn))
        {
            var row = stored.FirstOrDefault(l => l.Provider == provider);
            if (olderThan is null || row is null || now - row.CheckedAt >= olderThan)
            {
                await RefreshAsync(provider, ct);
            }
        }
    }

    /// <summary>The stored list, fetch time and last error per provider.</summary>
    public async Task<List<ProviderModelList>> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ProviderModelLists.AsNoTracking().ToListAsync(ct);
    }

    /// <summary>
    /// Whether <paramref name="list"/> serves <paramref name="upstreamModelId"/>: listed as-is, or as a dated snapshot
    /// (<c>claude-sonnet-4-5</c> is served as <c>claude-sonnet-4-5-20250929</c>).
    /// </summary>
    public static bool Lists(IReadOnlyCollection<string> list, string upstreamModelId) =>
        list.Contains(upstreamModelId) || list.Any(id => id.Length == upstreamModelId.Length + 9
            && id.StartsWith(upstreamModelId + "-", StringComparison.Ordinal)
            && id[(upstreamModelId.Length + 1)..].All(char.IsAsciiDigit));
}

/// <summary>Refreshes subscription model lists at startup when due, then every <see cref="RouterOptions.ModelListRefreshInterval"/>.</summary>
public sealed class ModelDiscoveryMonitor(ModelDiscovery discovery, IOptions<RouterOptions> options, TimeProvider time, ILogger<ModelDiscoveryMonitor> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.ModelListRefreshInterval;
        if (interval <= TimeSpan.Zero)
        {
            return;
        }
        await RefreshAsync(interval, stoppingToken);
        using var timer = new PeriodicTimer(interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(null, stoppingToken);
        }
    }

    private async Task RefreshAsync(TimeSpan? olderThan, CancellationToken ct)
    {
        try
        {
            await discovery.RefreshAllAsync(ct, olderThan);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Model list refresh failed: {error}", e.Message);
        }
    }
}
