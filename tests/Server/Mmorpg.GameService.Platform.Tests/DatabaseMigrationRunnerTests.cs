using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Platform.Tests;

// The migration runner on real databases: the lock timeout of a migration in a transaction,
// and the path of a migration that builds an index concurrently outside a transaction.
public sealed class DatabaseMigrationRunnerTests
{
    private static readonly DatabaseMigration CreateTable = new(
        "209901010000_test_table",
        "create table runner_test (id integer primary key, value integer not null);");

    private static readonly DatabaseMigration ConcurrentIndex = DatabaseMigration.CreateIndexConcurrently(
        "209901010100_test_index",
        "ix_runner_test_value",
        "create unique index concurrently ix_runner_test_value on runner_test(value);");

    [Fact]
    public void AConcurrentIndexMigrationIsOneStatementThatBuildsItsIndex()
    {
        Assert.True(ConcurrentIndex.RunsOutsideTransaction);
        Assert.Equal("ix_runner_test_value", ConcurrentIndex.ConcurrentIndex);
        Assert.False(CreateTable.RunsOutsideTransaction);

        Assert.Throws<ArgumentException>(() => DatabaseMigration.CreateIndexConcurrently(
            "209901010100_test_index",
            "ix_runner_test_value",
            "create index ix_runner_test_value on runner_test(value);"));
        Assert.Throws<ArgumentException>(() => DatabaseMigration.CreateIndexConcurrently(
            "209901010100_test_index",
            "ix_runner_test_value",
            "create index concurrently ix_other on runner_test(value);"));
        Assert.Throws<ArgumentException>(() => DatabaseMigration.CreateIndexConcurrently(
            "209901010100_test_index",
            "ix_runner_test_value",
            "create index concurrently ix_runner_test_value on runner_test(value); drop table runner_test;"));
        Assert.Throws<ArgumentException>(() => DatabaseMigration.CreateIndexConcurrently(
            "209901010100_test_index",
            "ix runner",
            "create index concurrently ix_runner_test_value on runner_test(value);"));
    }

    [PostgresIntegrationFact]
    public async Task AFreshDatabaseHasEveryMigrationPendingAndAnAppliedOneNone()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var runner = Runner(dataSource);

        var pendingBefore = await runner.PendingAsync([CreateTable, ConcurrentIndex], Token);
        var applied = await runner.ApplyAsync([CreateTable, ConcurrentIndex], Token);
        var pendingAfter = await runner.PendingAsync([CreateTable, ConcurrentIndex], Token);
        var appliedAgain = await runner.ApplyAsync([CreateTable, ConcurrentIndex], Token);

        Assert.Equal([CreateTable.Id, ConcurrentIndex.Id], pendingBefore);
        Assert.Equal([CreateTable.Id, ConcurrentIndex.Id], applied);
        Assert.Empty(pendingAfter);
        Assert.Empty(appliedAgain);
        Assert.True(await IndexIsValidAsync(dataSource));
        Assert.Equal([CreateTable.Id, ConcurrentIndex.Id], await RecordedAsync(dataSource));
    }

    // A unique index over duplicate values fails its concurrent build and stays behind invalid,
    // as an interrupted build does. The next run drops it and builds it again.
    [PostgresIntegrationFact]
    public async Task AFailedConcurrentBuildIsDroppedAndBuiltAgainByTheNextRun()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var runner = Runner(dataSource);
        await runner.ApplyAsync([CreateTable], Token);
        await ExecuteAsync(dataSource, "insert into runner_test (id, value) values (1, 7), (2, 7);");

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => runner.ApplyAsync([CreateTable, ConcurrentIndex], Token));
        var validAfterFailure = await IndexIsValidAsync(dataSource);
        var recordedAfterFailure = await RecordedAsync(dataSource);
        await ExecuteAsync(dataSource, "delete from runner_test where id = 2;");
        var applied = await runner.ApplyAsync([CreateTable, ConcurrentIndex], Token);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
        Assert.False(validAfterFailure);
        Assert.Equal([CreateTable.Id], recordedAfterFailure);
        Assert.Equal([ConcurrentIndex.Id], applied);
        Assert.True(await IndexIsValidAsync(dataSource));
        Assert.Equal([CreateTable.Id, ConcurrentIndex.Id], await RecordedAsync(dataSource));
    }

    // A run that built the index but stopped before it recorded the migration leaves a valid
    // index, which the next run keeps.
    [PostgresIntegrationFact]
    public async Task AValidIndexOfAnUnrecordedRunIsKeptAndRecorded()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var runner = Runner(dataSource);
        await runner.ApplyAsync([CreateTable], Token);
        await ExecuteAsync(dataSource, ConcurrentIndex.Sql);
        var indexBefore = await IndexOidAsync(dataSource);

        var applied = await runner.ApplyAsync([CreateTable, ConcurrentIndex], Token);

        Assert.Equal([ConcurrentIndex.Id], applied);
        Assert.Equal(indexBefore, await IndexOidAsync(dataSource));
        Assert.Equal([CreateTable.Id, ConcurrentIndex.Id], await RecordedAsync(dataSource));
    }

    // A transaction that read the table holds a lock that the change of the table must wait
    // for. Without the timeout the migration would queue behind it for as long as it runs.
    [PostgresIntegrationFact]
    public async Task AMigrationThatCannotGetItsTableLockFailsFastAndAppliesNothing()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var runner = Runner(dataSource, TimeSpan.FromMilliseconds(200));
        await runner.ApplyAsync([CreateTable], Token);
        var alter = new DatabaseMigration(
            "209901010200_test_column",
            "alter table runner_test add column note text;");
        await using var traffic = await dataSource.OpenConnectionAsync(Token);
        await using var trafficTransaction = await traffic.BeginTransactionAsync(Token);
        await traffic.ExecuteAsync("select count(*) from runner_test;", transaction: trafficTransaction);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ApplyAsync([CreateTable, alter], timeout.Token));
        await trafficTransaction.RollbackAsync(Token);

        Assert.Contains(alter.Id, exception.Message, StringComparison.Ordinal);
        Assert.Contains("200 ms", exception.Message, StringComparison.Ordinal);
        Assert.Contains(PlatformOptions.MigrationLockTimeoutKey, exception.Message, StringComparison.Ordinal);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
        Assert.Equal([CreateTable.Id], await RecordedAsync(dataSource));
        await using var connection = await dataSource.OpenConnectionAsync(Token);
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from information_schema.columns where table_name = 'runner_test' and column_name = 'note';"));
    }

    // Another migrating process holds the migration lock longer than the lock timeout; the
    // migrations wait for it instead of failing.
    [PostgresIntegrationFact]
    public async Task WaitingForAnotherMigratingProcessIsNotALockTimeout()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var runner = Runner(dataSource, TimeSpan.FromMilliseconds(100));
        await runner.ApplyAsync([CreateTable], Token);
        await using var other = await dataSource.OpenConnectionAsync(Token);
        await other.ExecuteAsync(
            "select pg_advisory_lock(@LockId);",
            new { LockId = DatabaseMigrationRunner.MigrationLockId });

        var apply = runner.ApplyAsync(
            [CreateTable, new DatabaseMigration("209901010200_test_column", "alter table runner_test add column note text;"), ConcurrentIndex],
            Token);
        await Task.Delay(TimeSpan.FromSeconds(1), Token);
        var finishedWhileLocked = apply.IsCompleted;
        await other.ExecuteAsync(
            "select pg_advisory_unlock(@LockId);",
            new { LockId = DatabaseMigrationRunner.MigrationLockId });
        var applied = await apply;

        Assert.False(finishedWhileLocked);
        Assert.Equal(["209901010200_test_column", ConcurrentIndex.Id], applied);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DatabaseMigrationRunner Runner(NpgsqlDataSource dataSource, TimeSpan? lockTimeout = null)
    {
        var options = new PlatformOptions(
            dataSource.ConnectionString,
            "127.0.0.1:6379",
            TimeSpan.FromSeconds(1),
            RunMigrationsOnStartup: true)
        {
            MigrationLockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5)
        };
        return new DatabaseMigrationRunner(dataSource, options, NullLogger<DatabaseMigrationRunner>.Instance);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<bool?> IndexIsValidAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync(Token);
        return await connection.ExecuteScalarAsync<bool?>(
            "select entry.indisvalid from pg_index entry where entry.indexrelid = to_regclass('ix_runner_test_value');");
    }

    private static async Task<long?> IndexOidAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync(Token);
        return await connection.ExecuteScalarAsync<long?>("select to_regclass('ix_runner_test_value')::oid::bigint;");
    }

    private static async Task<string[]> RecordedAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync(Token);
        return (await connection.QueryAsync<string>("select id from schema_migrations order by id;")).ToArray();
    }
}
