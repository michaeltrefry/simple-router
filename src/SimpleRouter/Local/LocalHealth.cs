using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.Local;

/// <summary>
/// Health of enabled local endpoints, probed with <c>GET {baseUrl}/models</c>. A model counts as healthy only after a
/// successful probe, so new sessions never land on an endpoint that has not been checked.
/// </summary>
public sealed class LocalHealth(
    IHttpClientFactory httpClientFactory,
    IDbContextFactory<RouterDbContext> dbFactory,
    SecretProtector secrets,
    ILogger<LocalHealth> logger)
{
    public const string HttpClientName = "local-health";

    private readonly ConcurrentDictionary<string, bool> _healthy = new();

    // A check that read the catalog before a settings edit must not finish after the edit's own check and undo it.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsHealthy(string modelId) => _healthy.TryGetValue(modelId, out var healthy) && healthy;

    public void MarkUnhealthy(string modelId) => Set(modelId, false);

    public async Task CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await CheckAllAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="commit"/>, which renames a model in the catalog, with <paramref name="oldId"/>'s state already
    /// under <paramref name="newId"/> and no check in flight, so the renamed model is never unprobed and its sessions stay pinned.
    /// </summary>
    public async Task RenameAsync(string oldId, string newId, Func<Task> commit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_healthy.TryGetValue(oldId, out var healthy))
            {
                _healthy[newId] = healthy;
            }
            try
            {
                await commit();
            }
            catch
            {
                _healthy.TryRemove(newId, out _);
                throw;
            }
            _healthy.TryRemove(oldId, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Probes an endpoint that may not be in the catalog yet; returns why it failed, or null when it answered.</summary>
    public async Task<string?> TestAsync(string baseUrl, string? apiKey, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/models";
        try
        {
            using var response = await SendProbeAsync(url, apiKey, ct);
            return response.IsSuccessStatusCode ? null : $"GET {url} answered HTTP {(int)response.StatusCode}.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"GET {url} failed: the endpoint is unreachable or timed out.";
        }
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        List<CatalogModel> models;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            models = await db.Models.AsNoTracking()
                .Where(m => m.Provider == ProviderKind.Local && m.Enabled && m.BaseUrl != null)
                .ToListAsync(ct);
        }

        var results = await Task.WhenAll(models.Select(async m => (m.Id, Healthy: await ProbeAsync(m, ct))));
        foreach (var (id, healthy) in results)
        {
            Set(id, healthy);
        }
        foreach (var stale in _healthy.Keys.Except(results.Select(r => r.Id)))
        {
            _healthy.TryRemove(stale, out _);
        }
    }

    private async Task<bool> ProbeAsync(CatalogModel model, CancellationToken ct)
    {
        var key = model.ProtectedApiKey is { } protectedKey ? secrets.Unprotect(protectedKey) : null;
        try
        {
            using var response = await SendProbeAsync(LocalUpstream.Endpoint(model, "models"), key, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendProbeAsync(string url, string? apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        return await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
    }

    private void Set(string modelId, bool healthy)
    {
        if (!_healthy.TryGetValue(modelId, out var previous) || previous != healthy)
        {
            logger.LogInformation("Local model {model} is {state}", modelId, healthy ? "healthy" : "unhealthy");
        }
        _healthy[modelId] = healthy;
    }
}

/// <summary>
/// Runs <see cref="LocalHealth.CheckAsync"/> once before the server starts, then every configured interval. Probing
/// before serving matters: an unprobed local is ineligible, so early requests would otherwise re-pin local sessions.
/// </summary>
public sealed class LocalHealthMonitor(LocalHealth health, Microsoft.Extensions.Options.IOptions<RouterOptions> options, ILogger<LocalHealthMonitor> logger)
    : BackgroundService, IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => CheckAsync(cancellationToken);

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = options.Value.Local.HealthCheckIntervalSeconds;
        if (seconds <= 0)
        {
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckAsync(stoppingToken);
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            await health.CheckAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Local health check failed: {error}", e.Message);
        }
    }
}
