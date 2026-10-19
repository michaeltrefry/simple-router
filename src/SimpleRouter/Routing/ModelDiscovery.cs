using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleRouter.Data;
using SimpleRouter.OpenRouter;

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
            IReadOnlyList<OpenRouterModelInfo>? metadata = null;
            try
            {
                if (source is OpenRouterModelList openRouter)
                {
                    metadata = await openRouter.FetchModelsAsync(ct);
                    ids = metadata.Select(m => m.Id).ToList();
                }
                else
                {
                    ids = await source.FetchAsync(ct);
                }
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

            ProviderModelList row;
            try
            {
                row = await SaveAsync(provider, ids, metadata, error, ct);
            }
            catch (DbUpdateException)
            {
                // Another process (a CLI login beside the running server) may have inserted the first row meanwhile.
                row = await SaveAsync(provider, ids, metadata, error, ct);
            }
            if (error is null)
            {
                logger.LogInformation("Fetched the {provider} model list: {count} models", provider, row.ModelIds!.Count);
                await WarnUnlistedAsync(provider, row.ModelIds!, ct);
            }
            else
            {
                logger.LogWarning("Could not fetch the {provider} model list; keeping the last good list: {error}", provider, error);
            }
            return row;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One warning naming the enabled models the fetched list leaves out; they stay ineligible until it lists them.</summary>
    private async Task WarnUnlistedAsync(ProviderKind provider, List<string> listed, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var missing = (await db.Models.AsNoTracking().Where(m => m.Provider == provider && m.Enabled).OrderBy(m => m.Id).ToListAsync(ct))
            .Where(m => !Lists(provider, listed, m.UpstreamModelId))
            .Select(m => m.Id)
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }
        var hint = provider == ProviderKind.Codex
            ? " The Codex backend hides models that need a newer client; raise --SimpleRouter:Codex:ClientVersion if they should be listed."
            : "";
        logger.LogWarning("The {provider} model list does not include these configured models, so they are not selected: {models}.{hint}",
            provider, string.Join(", ", missing), hint);
    }

    private async Task<ProviderModelList> SaveAsync(ProviderKind provider, IReadOnlyList<string>? ids, IReadOnlyList<OpenRouterModelInfo>? metadata, string? error, CancellationToken ct)
    {
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
            if (metadata is not null)
                await UpdateOpenRouterModelsAsync(db, metadata, ct);
        }
        await db.SaveChangesAsync(ct);
        return row;
    }

    private static async Task UpdateOpenRouterModelsAsync(RouterDbContext db, IReadOnlyList<OpenRouterModelInfo> metadata, CancellationToken ct)
    {
        var models = await db.Models.Where(m => m.Provider == ProviderKind.OpenRouter).ToListAsync(ct);
        var prices = await db.ModelPrices.ToDictionaryAsync(p => p.ModelId, ct);
        var discovered = metadata.DistinctBy(m => m.Id).ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var model in models)
        {
            // Auto routers choose a different model per request; their price and capabilities are not fixed.
            if (OpenRouterModelList.IsAuto(model.UpstreamModelId) || !discovered.TryGetValue(model.UpstreamModelId, out var info))
                continue;
            if (info.ContextWindow is { } context) model.ContextWindow = context;
            if (info.SupportsTools is { } tools) model.SupportsTools = tools;
            if (info.SupportsVision is { } vision) model.SupportsVision = vision;
            if (info.Effort is { } effort) model.Effort ??= effort;
            prices.TryGetValue(model.Id, out var price);
            // A partial or dynamic price cannot estimate a request: neither zero nor the old price is valid.
            if (info.InputPerMTok is null || info.OutputPerMTok is null)
            {
                if (price is not null) db.ModelPrices.Remove(price);
                continue;
            }
            if (price is null)
            {
                price = new ModelPrice { ModelId = model.Id };
                db.ModelPrices.Add(price);
            }
            if (info.InputPerMTok is { } input) price.InputPerMTok = input;
            if (info.OutputPerMTok is { } output) price.OutputPerMTok = output;
            if (info.CacheReadPerMTok is { } read) price.CacheReadPerMTok = read;
            if (info.CacheWritePerMTok is { } write) price.CacheWritePerMTok = write;
            if (info.CacheWrite1hPerMTok is { } write1h) price.CacheWrite1hPerMTok = write1h;
        }
    }

    /// <summary>Fetches right after a login and tells the user the outcome; a failed fetch or save does not fail the login.</summary>
    public async Task ReportRefreshAsync(ProviderKind provider, TextWriter output, CancellationToken ct)
    {
        ProviderModelList list;
        try
        {
            list = await RefreshAsync(provider, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            list = new ProviderModelList { Provider = provider, LastError = e.Message };
        }
        await output.WriteLineAsync(list.LastError is null
            ? $"Found {list.ModelIds!.Count} {provider} models."
            : $"Could not fetch the {provider} model list ({list.LastError}); the running router retries it.");
    }

    /// <summary>Fetches every logged-in subscription's list now and tells the user each outcome; false when none is logged in.</summary>
    public async Task<bool> ReportRefreshLoggedInAsync(TextWriter output, CancellationToken ct = default)
    {
        Func<ProviderKind, bool> loggedIn;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            loggedIn = await ModelSelector.LoggedInAsync(db, ct);
        }
        var providers = sources.Select(s => s.Provider).Where(loggedIn).ToList();
        if (providers.Count == 0)
        {
            await output.WriteLineAsync("No subscription is logged in; run 'simple-router login claude' or 'simple-router login codex' first.");
            return false;
        }
        foreach (var provider in providers)
        {
            await ReportRefreshAsync(provider, output, ct);
        }
        return true;
    }

    /// <summary>Refreshes every logged-in subscription, skipping those fetched successfully within <paramref name="olderThan"/>.</summary>
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
            if (olderThan is null || row?.FetchedAt is null || now - row.FetchedAt >= olderThan)
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
    /// Whether <paramref name="list"/> serves <paramref name="upstreamModelId"/>, ignoring case: listed as-is, or with only a
    /// snapshot date between them in either direction (<c>claude-sonnet-4-5</c> and <c>claude-sonnet-4-5-20250929</c>).
    /// </summary>
    public static bool Lists(IReadOnlyCollection<string> list, string upstreamModelId) =>
        list.Contains(upstreamModelId, StringComparer.OrdinalIgnoreCase)
        || list.Any(id => IsSnapshotOf(id, upstreamModelId) || IsSnapshotOf(upstreamModelId, id));

    public static bool Lists(ProviderKind provider, IReadOnlyCollection<string> list, string upstreamModelId) =>
        provider == ProviderKind.OpenRouter
            ? OpenRouterModelList.IsAuto(upstreamModelId) || list.Contains(upstreamModelId, StringComparer.Ordinal)
            : Lists(list, upstreamModelId);

    private static bool IsSnapshotOf(string dated, string alias) =>
        dated.Length == alias.Length + 9
        && dated.StartsWith(alias + "-", StringComparison.OrdinalIgnoreCase)
        && dated[(alias.Length + 1)..].All(char.IsAsciiDigit);
}

/// <summary>
/// Refreshes subscription model lists not fetched within <see cref="RouterOptions.ModelListRefreshInterval"/> at startup,
/// then when the earliest skipped one comes due and every interval after that.
/// </summary>
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
        // Created before the startup fetch so the schedule never depends on how long that fetch takes.
        using var timer = new PeriodicTimer(await FirstDelayAsync(interval, stoppingToken), time);
        await RefreshAsync(interval, stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (timer.Period != interval)
            {
                timer.Period = interval;
            }
            await RefreshAsync(null, stoppingToken);
        }
    }

    /// <summary>Time until the earliest list skipped at startup turns <paramref name="interval"/> old.</summary>
    private async Task<TimeSpan> FirstDelayAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            var now = time.GetUtcNow();
            return (await discovery.GetAsync(ct))
                .Where(l => l.FetchedAt is not null)
                .Select(l => interval - (now - l.FetchedAt!.Value))
                .Where(d => d > TimeSpan.Zero && d < interval)
                .DefaultIfEmpty(interval)
                .Min();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not read the stored model lists: {error}", e.Message);
            return interval;
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
