using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SimpleRouter.Data;

/// <summary>Lets <c>dotnet ef</c> build the model without starting the host.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RouterDbContext>
{
    public RouterDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RouterDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
