using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Gateway;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class GatewayKeyStoreTests
{
    [Fact]
    public async Task First_run_generates_a_key_that_later_runs_reuse_and_that_is_encrypted_at_rest()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var args = RouterHost.Args(dataDirectory);

        var first = RouterApp.Build(args);
        await RouterApp.InitializeAsync(first);
        var firstKey = await first.Services.GetRequiredService<GatewayKeyStore>().GetAsync();
        await first.DisposeAsync();

        var second = RouterApp.Build(args);
        await RouterApp.InitializeAsync(second);
        var store = second.Services.GetRequiredService<GatewayKeyStore>();
        Assert.False(await store.EnsureAsync());
        Assert.Equal(firstKey, await store.GetAsync());
        await second.DisposeAsync();

        Assert.StartsWith("sr_", firstKey);
        Assert.True(firstKey.Length >= 40);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "simple-router.db")}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = 'gateway_key'";
        var stored = (string)(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain(firstKey, stored);
        SqliteConnection.ClearPool(connection);
    }
}
