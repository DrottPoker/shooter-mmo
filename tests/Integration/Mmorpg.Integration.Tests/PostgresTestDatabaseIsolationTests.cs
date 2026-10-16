using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.Integration.Tests;

// Guards the per-test databases that let PostgreSQL tests run in parallel: every lease is a
// fresh copy of its template, and nothing one test writes or leaves open reaches the next.
public sealed class PostgresTestDatabaseIsolationTests
{
    [PostgresIntegrationFact]
    public async Task ConcurrentContextsOwnSeparateInitializedDatabases()
    {
        var contexts = await Task.WhenAll(
            PostgresIntegrationTestContext.CreateAsync(),
            PostgresIntegrationTestContext.CreateAsync());
        await using var first = contexts[0];
        await using var second = contexts[1];

        Assert.NotEqual(DatabaseOf(first.ConnectionString), DatabaseOf(second.ConnectionString));
        await first.RegisterPlayerAsync();

        Assert.Equal(1, await first.ExecuteScalarIntAsync("select count(*) from accounts;"));
        Assert.Equal(0, await second.ExecuteScalarIntAsync("select count(*) from accounts;"));
        Assert.Equal(
            await first.ExecuteScalarIntAsync("select count(*) from schema_migrations;"),
            await second.ExecuteScalarIntAsync("select count(*) from schema_migrations;"));
        Assert.Equal(
            1,
            await second.ExecuteScalarIntAsync(
                "select count(*) from item_catalog_revisions where is_current;"));
    }

    [PostgresIntegrationFact]
    public async Task ReleasedDatabaseIsRecreatedEvenWithAConnectionLeftOpen()
    {
        var released = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        var leftOpen = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(released.ConnectionString) { Pooling = false }.ConnectionString);
        await leftOpen.OpenAsync();
        await using (var create = new NpgsqlCommand("create table leftover (id integer);", leftOpen))
        {
            await create.ExecuteNonQueryAsync();
        }

        await released.DisposeAsync();
        await using var next = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);

        // This assembly runs one test at a time and lease numbers are reused lowest first,
        // so the next lease recreated the database that was just released.
        Assert.Equal(DatabaseOf(released.ConnectionString), DatabaseOf(next.ConnectionString));
        await using var dataSource = NpgsqlDataSource.Create(next.ConnectionString);
        await using var tables = dataSource.CreateCommand(
            "select count(*) from information_schema.tables where table_schema = 'public';");
        Assert.Equal(0L, await tables.ExecuteScalarAsync());
        await using (var probe = new NpgsqlCommand("select 1;", leftOpen))
        {
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => probe.ExecuteScalarAsync());
        }

        await leftOpen.DisposeAsync();
    }

    private static string DatabaseOf(string connectionString)
    {
        return new NpgsqlConnectionStringBuilder(connectionString).Database!;
    }
}
