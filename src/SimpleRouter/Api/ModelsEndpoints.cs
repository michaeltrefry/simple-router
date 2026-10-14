using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.Api;

public static class ModelsEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 1000;

    public static void MapModelsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", async (HttpContext context, IDbContextFactory<RouterDbContext> dbFactory, ModelSelector selector, string? limit) =>
        {
            var take = DefaultLimit;
            if ((limit is not null && !int.TryParse(limit, out take)) || take is < 1 or > MaxLimit)
            {
                await AnthropicError.WriteAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error",
                    $"limit must be between 1 and {MaxLimit}.");
                return;
            }

            var ids = await AdvertisedAsync(dbFactory, selector, context.RequestAborted);
            var page = ids.Take(take).ToList();
            await context.Response.WriteAsJsonAsync(new
            {
                data = page.Select(id => new
                {
                    type = "model",
                    id,
                    display_name = id,
                    created_at = DateTimeOffset.UnixEpoch,
                }),
                has_more = ids.Count > page.Count,
                first_id = page.FirstOrDefault(),
                last_id = page.LastOrDefault(),
            });
        });
    }

    /// <summary>
    /// Claude catalog models an alias maps to a tier that has an enabled member, highest tier first, so a client's model
    /// picker offers every servable tier and each ID, sent back as <c>model</c>, is served in that tier. A disabled Claude
    /// model still names its tier while another member serves it.
    /// </summary>
    private static async Task<List<string>> AdvertisedAsync(IDbContextFactory<RouterDbContext> dbFactory, ModelSelector selector, CancellationToken ct)
    {
        var rules = await selector.GetTierRulesAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.Models.AsNoTracking().Where(m => m.Provider == ProviderKind.Claude).Select(m => m.Id).ToListAsync(ct);
        var servable = (await db.TierMembers.AsNoTracking()
                .Join(db.Models.Where(m => m.Enabled), t => t.ModelId, m => m.Id, (t, _) => t.Tier)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
        return ids
            .Select(id => (Id: id, Tier: TierResolver.AliasTier(id, rules)))
            .Where(m => m.Tier is { } tier && servable.Contains(tier))
            .OrderBy(m => m.Tier)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => m.Id)
            .ToList();
    }
}
