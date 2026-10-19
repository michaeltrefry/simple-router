using System.Text.Json.Nodes;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleRouter.OpenRouter;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using SimpleRouter.Usage;

namespace SimpleRouter.Tests;

public class OpenRouterUsageTests
{
    [Fact]
    public void Metadata_is_merged_without_losing_zero_cost_and_reset_between_attempts()
    {
        var context = new DefaultHttpContext();
        UpstreamUsageMetadata.Capture(context, JsonNode.Parse("""{"id":"gen-test","model":"vendor/resolved"}""")!.AsObject());
        UpstreamUsageMetadata.Capture(context, JsonNode.Parse("""{"choices":[],"usage":{"cost":0}}""")!.AsObject());
        Assert.Equal(new UpstreamUsageMetadata("vendor/resolved", "gen-test", 0), UpstreamUsageMetadata.Get(context));
        UpstreamUsageMetadata.Capture(context, JsonNode.Parse("""{"usage":{"cost":-1}}""")!.AsObject());
        Assert.Equal(0m, UpstreamUsageMetadata.Get(context)!.CostUsd);
        UpstreamUsageMetadata.Reset(context);
        Assert.Null(UpstreamUsageMetadata.Get(context));
    }

    [Fact]
    public async Task Reported_cost_overrides_configured_price_and_resolved_metadata_does_not_change_entry()
    {
        await using var host = await RouterHost.StartAsync();
        var recorder = host.Services.GetRequiredService<UsageRecorder>();
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ModelPrices.Add(new ModelPrice { ModelId = "auto-test", InputPerMTok = 999, OutputPerMTok = 999 });
            await db.SaveChangesAsync();
        }
        await recorder.RecordAsync(new ServedRequest("session", Auto(), ModelTier.High, 200,
            new TokenUsage(1000, 2000, 0, 0), TimeSpan.FromSeconds(1), "high",
            new UpstreamUsageMetadata("vendor/resolved", "gen-test", 0.0012345m)));
        await using var check = await factory.CreateDbContextAsync();
        var row = Assert.Single(await check.RequestUsages.ToListAsync());
        Assert.Equal("auto-test", row.ModelId);
        Assert.Equal("vendor/resolved", row.ResolvedModelId);
        Assert.Equal("gen-test", row.GenerationId);
        Assert.Equal("reported", row.CostSource);
        Assert.Equal(1235, row.CostUsdMicros);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("openrouter/auto")]
    [InlineData("openrouter/auto-beta")]
    public async Task An_unresolved_auto_target_is_unknown_even_when_the_entry_has_a_price(string? resolved)
    {
        await using var host = await RouterHost.StartAsync();
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ModelPrices.Add(new ModelPrice { ModelId = "auto-test", InputPerMTok = 1, OutputPerMTok = 2 });
            await db.SaveChangesAsync();
        }
        await host.Services.GetRequiredService<UsageRecorder>().RecordAsync(new ServedRequest(null, Auto(), ModelTier.High, 200,
            new TokenUsage(10, 20, 0, 0), TimeSpan.Zero, null, new UpstreamUsageMetadata(resolved, null, null)));
        await using var check = await factory.CreateDbContextAsync();
        Assert.Equal("unknown", (await check.RequestUsages.SingleAsync()).CostSource);
    }

    [Fact]
    public async Task Missing_reported_cost_uses_resolved_price_or_is_explicitly_unknown()
    {
        await using var host = await RouterHost.StartAsync();
        var recorder = host.Services.GetRequiredService<UsageRecorder>();
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Models.Add(new CatalogModel { Id = "fixed-test", Provider = ProviderKind.OpenRouter, UpstreamModelId = "vendor/fixed" });
            db.ModelPrices.Add(new ModelPrice { ModelId = "fixed-test", InputPerMTok = 1, OutputPerMTok = 3 });
            await db.SaveChangesAsync();
        }
        foreach (var resolved in new[] { "vendor/fixed", "vendor/unknown" })
        {
            await recorder.RecordAsync(new ServedRequest("session", Auto(), ModelTier.High, 200,
                new TokenUsage(10, 20, 0, 0), TimeSpan.Zero, null, new UpstreamUsageMetadata(resolved, "gen-test", null)));
        }
        await using var check = await factory.CreateDbContextAsync();
        var rows = await check.RequestUsages.OrderBy(u => u.Id).ToListAsync();
        Assert.Equal("estimated", rows[0].CostSource);
        Assert.Equal(70, rows[0].CostUsdMicros);
        Assert.Equal("unknown", rows[1].CostSource);
        var total = (await recorder.SessionCostAsync("session", default))!.Value;
        Assert.Equal(1, total.UnknownCount);
        Assert.Equal(1, total.EstimatedCount);
    }

    [Fact]
    public async Task Failover_resets_metadata_and_keeps_the_configured_auto_entry_pin()
    {
        await using var host = await RouterHost.StartAsync(services =>
        {
            services.RemoveAll<IModelUpstream>();
            services.AddSingleton<IModelUpstream, FailoverUpstream>();
        });
        await host.Services.GetRequiredService<OpenRouterCredentialStore>().SetAsync("test-key");
        var factory = host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.TierMembers.RemoveRange(await db.TierMembers.Where(m => m.Tier == ModelTier.High).ToListAsync());
            db.Models.AddRange(new CatalogModel { Id = "attempt-a", Provider = ProviderKind.OpenRouter, UpstreamModelId = "openrouter/auto", Enabled = true },
                new CatalogModel { Id = "attempt-b", Provider = ProviderKind.OpenRouter, UpstreamModelId = "openrouter/auto-beta", Enabled = true });
            db.TierMembers.AddRange(new TierMember { Tier = ModelTier.High, ModelId = "attempt-a", Position = 0 },
                new TierMember { Tier = ModelTier.High, ModelId = "attempt-b", Position = 1 });
            await db.SaveChangesAsync();
        }
        var client = await host.AuthorizedClientAsync();
        client.DefaultRequestHeaders.Add(SessionKey.HeaderName, "metadata-failover");
        client.DefaultRequestHeaders.Add(TierResolver.HeaderName, "high");
        using var response = await client.PostAsJsonAsync("/v1/messages", new { model = "default", max_tokens = 10, messages = new[] { new { role = "user", content = "hi" } } });
        response.EnsureSuccessStatusCode();
        RequestUsage? row = null;
        for (var attempt = 0; attempt < 100 && row is null; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync();
            row = await db.RequestUsages.SingleOrDefaultAsync();
            if (row is null) await Task.Delay(50);
        }
        Assert.NotNull(row);
        Assert.Equal("attempt-b", row.ModelId);
        Assert.Null(row.ResolvedModelId);
        Assert.Null(row.GenerationId);
        Assert.Equal("unknown", row.CostSource);
        await using var check = await factory.CreateDbContextAsync();
        Assert.Equal("attempt-b", (await check.SessionPins.SingleAsync()).ModelId);
    }

    private sealed class FailoverUpstream : IModelUpstream
    {
        public ProviderKind Provider => ProviderKind.OpenRouter;
        public async Task<UpstreamResult> SendAsync(UpstreamCall call)
        {
            if (call.Model.Id == "attempt-a")
            {
                UpstreamUsageMetadata.Capture(call.Context, JsonNode.Parse("""{"id":"stale","model":"vendor/stale","usage":{"cost":99}}""")!.AsObject());
                return UpstreamResult.NotServed(new ModelFault(FaultKind.Overloaded));
            }
            await call.Context.Response.WriteAsJsonAsync(new { type = "message", usage = new { input_tokens = 2, output_tokens = 1 } });
            return UpstreamResult.Served;
        }
    }

    private static CatalogModel Auto() => new() { Id = "auto-test", Provider = ProviderKind.OpenRouter, UpstreamModelId = "openrouter/auto" };
}
