using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.Tests;

public class ModelCooldownsTests
{
    /// <summary>Every save fails as if another request had just inserted the same row.</summary>
    private sealed class ConflictingSaves : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new DbUpdateException("UNIQUE constraint failed");
        }
    }

    private sealed class Factory(DbContextOptions<RouterDbContext> options) : IDbContextFactory<RouterDbContext>
    {
        public RouterDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task A_cooldown_that_cannot_be_saved_even_on_retry_is_dropped_without_failing_the_request()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var saves = new ConflictingSaves();
        var options = new DbContextOptionsBuilder<RouterDbContext>().UseSqlite(connection).AddInterceptors(saves).Options;
        await using (var db = new RouterDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }
        var cooldowns = new ModelCooldowns(new Factory(options), Options.Create(new RouterOptions()), TimeProvider.System, NullLogger<ModelCooldowns>.Instance);
        var model = new CatalogModel { Id = "claude-a", Provider = ProviderKind.Claude, UpstreamModelId = "upstream-a" };

        await cooldowns.RecordAsync(model, new ModelFault(FaultKind.Limited));

        Assert.Equal(2, saves.Attempts);
    }
}
