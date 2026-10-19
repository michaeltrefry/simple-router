using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.OpenRouter;
using SimpleRouter.Routing;

namespace SimpleRouter.Dashboard;

public sealed partial class CatalogSettings
{
    public Task<bool> HasOpenRouterKeyAsync(CancellationToken ct = default) =>
        openRouterCredentials.HasKeyAsync(ct);

    public async Task SetOpenRouterKeyAsync(string? apiKey, CancellationToken ct = default)
    {
        await openRouterCredentials.SetAsync(apiKey, ct);
        events.Publish();
    }

    public async Task SaveOpenRouterModelAsync(OpenRouterModelInput input, string? existingId, CancellationToken ct = default)
    {
        var id = ValidModelId(input.Id, "Model ID");
        var upstream = ValidModelId(input.UpstreamModelId, "Upstream model ID");
        if (input.ContextWindow is <= 0)
            throw new SettingsException("Context window must be positive, or empty for unlimited.");
        ValidEffort(input.Effort, id);
        try { OpenRouterRoutingOptions.Validate(input.OpenRouter); }
        catch (InvalidDataException e) { throw new SettingsException(e.Message); }
        if (!OpenRouterRoutingOptions.IsAuto(upstream) && input.OpenRouter is { } o
            && (o.CostTier is not null || o.AllowedModels?.Count > 0 || o.ExcludedModels?.Count > 0))
            throw new SettingsException("Auto-routing constraints apply only to openrouter/auto or openrouter/auto-beta, not fixed models.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        CatalogModel model;
        if (existingId is null)
        {
            if (await db.Models.AnyAsync(m => m.Id == id, ct))
                throw new SettingsException($"A model with ID '{id}' already exists.");
            model = new CatalogModel { Id = id, Provider = ProviderKind.OpenRouter, UpstreamModelId = upstream };
            db.Models.Add(model);
        }
        else
        {
            model = await FindAsync(db, existingId, ct);
            if (model.Provider != ProviderKind.OpenRouter)
                throw new SettingsException($"'{existingId}' is not an OpenRouter model.");
            if (id != existingId) model = await RenameAsync(db, model, id, ct);
        }
        if (model.UpstreamModelId != upstream)
            await db.ModelPrices.Where(p => p.ModelId == id).ExecuteDeleteAsync(ct);
        model.UpstreamModelId = upstream;
        model.Enabled = input.Enabled;
        model.ContextWindow = input.ContextWindow;
        model.SupportsTools = input.SupportsTools;
        model.SupportsVision = input.SupportsVision;
        model.Effort = input.Effort;
        model.OpenRouter = input.OpenRouter;
        var current = await db.TierMembers.Where(m => m.ModelId == id).ToListAsync(ct);
        db.TierMembers.RemoveRange(current.Where(m => !input.Tiers.Contains(m.Tier)));
        foreach (var tier in input.Tiers.Distinct().Where(t => current.All(m => m.Tier != t)))
            await AddMemberAsync(db, tier, id, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        events.Publish();
    }
}
