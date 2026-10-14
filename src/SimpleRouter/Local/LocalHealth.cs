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

    public bool IsHealthy(string modelId) => _healthy.TryGetValue(modelId, out var healthy) && healthy;

    public void MarkUnhealthy(string modelId) => Set(modelId, false);

    public async Task CheckAsync(CancellationToken ct)
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
        using var request = new HttpRequestMessage(HttpMethod.Get, LocalUpstream.Endpoint(model, "models"));
        if (model.ProtectedApiKey is { } key)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Unprotect(key));
        }
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
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

/// <summary>Runs <see cref="LocalHealth.CheckAsync"/> at start and then every configured interval.</summary>
public sealed class LocalHealthMonitor(LocalHealth health, Microsoft.Extensions.Options.IOptions<RouterOptions> options, ILogger<LocalHealthMonitor> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = options.Value.Local.HealthCheckIntervalSeconds;
        if (seconds <= 0)
        {
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do
        {
            try
            {
                await health.CheckAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Local health check failed: {error}", e.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
