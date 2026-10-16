using Microsoft.EntityFrameworkCore;
using SimpleRouter.Dashboard;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.Usage;

/// <summary>A served request to record: the model and tier that answered, the response status and its usage.</summary>
public sealed record ServedRequest(
    string? ClientSessionId, CatalogModel Model, ModelTier? Tier, int Status, TokenUsage Usage, TimeSpan Latency, string? Effort);

/// <summary>Prices a served request from <see cref="RouterDbContext.ModelPrices"/> and stores it as a <see cref="RequestUsage"/> row.</summary>
public sealed class UsageRecorder(
    IDbContextFactory<RouterDbContext> dbFactory, DashboardEvents events, TimeProvider time, ILogger<UsageRecorder> logger)
{
    public async Task RecordAsync(ServedRequest served)
    {
        // Not the request's token: the response is already sent, and a client that disconnects still spent the tokens.
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var price = await db.ModelPrices.AsNoTracking().FirstOrDefaultAsync(p => p.ModelId == served.Model.Id);
            db.RequestUsages.Add(new RequestUsage
            {
                At = time.GetUtcNow(),
                ClientSessionId = served.ClientSessionId,
                ModelId = served.Model.Id,
                Provider = served.Model.Provider,
                Tier = served.Tier,
                Status = served.Status,
                InputTokens = served.Usage.InputTokens,
                OutputTokens = served.Usage.OutputTokens,
                CacheReadTokens = served.Usage.CacheReadTokens,
                CacheWriteTokens = served.Usage.CacheWriteTokens,
                CacheWrite1hTokens = served.Usage.CacheWrite1hTokens,
                CostUsdMicros = served.Usage.CostUsdMicros(price),
                LatencyMs = (long)served.Latency.TotalMilliseconds,
                Effort = served.Effort,
            });
            await db.SaveChangesAsync();
            events.Publish();
        }
        catch (Exception e)
        {
            // Recording runs after the response is sent (or while a cut-off one unwinds); it must never fail the request.
            logger.LogWarning("Could not record the usage of a request served by {model}: {error}", served.Model.Id, e.GetType().Name);
        }
    }

    /// <summary>The recorded requests and their summed cost for a client session ID; null when none is recorded.</summary>
    public async Task<(long Count, long CostUsdMicros)?> SessionCostAsync(string clientSessionId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var totals = await db.RequestUsages.Where(u => u.ClientSessionId == clientSessionId)
            .GroupBy(u => u.ClientSessionId)
            .Select(g => new { Count = g.LongCount(), Cost = g.Sum(u => u.CostUsdMicros) })
            .FirstOrDefaultAsync(ct);
        return totals is null ? null : (totals.Count, totals.Cost);
    }
}
