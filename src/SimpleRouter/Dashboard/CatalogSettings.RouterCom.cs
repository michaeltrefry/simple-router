using Microsoft.EntityFrameworkCore;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.RouterCom;

namespace SimpleRouter.Dashboard;

public sealed partial class CatalogSettings
{
    public Task<bool> HasRouterComKeyAsync(CancellationToken ct = default) =>
        routerComCredentials.HasKeyAsync(ct);

    public async Task SetRouterComKeyAsync(string? apiKey, CancellationToken ct = default)
    {
        await routerComCredentials.SetAsync(apiKey, ct);
        events.Publish();
    }

    public async Task SaveRouterComModelAsync(RouterComModelInput input, string? existingId, CancellationToken ct = default)
    {
        var id = ValidModelId(input.Id, "Model ID");
        var upstream = input.UpstreamModelId?.Trim() ?? "";
        if (!ModelIdPattern().IsMatch(upstream))
            throw new SettingsException("Upstream model ID must be 1-200 letters, digits or . _ : / @ - and start with a letter or digit.");
        if (input.ContextWindow is <= 0)
            throw new SettingsException("Context window must be positive, or empty for unlimited.");

        try { RouterComRoutingOptions.Validate(input.RouterCom); }
        catch (InvalidDataException e) { throw new SettingsException(e.Message); }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        CatalogModel model;
        if (existingId is null)
        {
            if (await db.Models.AnyAsync(m => m.Id == id, ct))
                throw new SettingsException($"A model with ID '{id}' already exists.");
            model = new CatalogModel { Id = id, Provider = ProviderKind.RouterCom, UpstreamModelId = upstream };
            db.Models.Add(model);
        }
        else
        {
            model = await FindAsync(db, existingId, ct);
            if (model.Provider != ProviderKind.RouterCom)
                throw new SettingsException($"'{existingId}' is not a Router.com model.");
            if (id != existingId) model = await RenameAsync(db, model, id, ct);
        }
        if (model.UpstreamModelId != upstream)
            await db.ModelPrices.Where(p => p.ModelId == id).ExecuteDeleteAsync(ct);
        model.UpstreamModelId = upstream;
        model.Enabled = input.Enabled;
        model.ContextWindow = input.ContextWindow;
        model.SupportsTools = input.SupportsTools;
        model.SupportsVision = input.SupportsVision;
        model.RouterCom = input.RouterCom;
        var current = await db.TierMembers.Where(m => m.ModelId == id).ToListAsync(ct);
        db.TierMembers.RemoveRange(current.Where(m => !input.Tiers.Contains(m.Tier)));
        foreach (var tier in input.Tiers.Distinct().Where(t => current.All(m => m.Tier != t)))
            await AddMemberAsync(db, tier, id, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        events.Publish();
    }
}
