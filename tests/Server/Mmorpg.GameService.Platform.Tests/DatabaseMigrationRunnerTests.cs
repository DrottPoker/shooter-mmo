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

        var pendingBefore = (await runner.PlanAsync([CreateTable, ConcurrentIndex], Token)).Pending;
        var applied = (await runner.ApplyAsync([CreateTable, ConcurrentIndex], Token)).Applied;
        var pendingAfter = (await runner.PlanAsync([CreateTable, ConcurrentIndex], Token)).Pending;
        var appliedAgain = (await runner.ApplyAsync([CreateTable, ConcurrentIndex], Token)).Applied;

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
        var applied = (await runner.ApplyAsync([CreateTable, ConcurrentIndex], Token)).Applied;

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

        var applied = (await runner.ApplyAsync([CreateTable, ConcurrentIndex], Token)).Applied;

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
        var applied = (await apply).Applied;

        Assert.False(finishedWhileLocked);
        Assert.Equal(["209901010200_test_column", ConcurrentIndex.Id], applied);
    }

    // Npgsql ends every command that has no timeout of its own after the connection string's
    // Command Timeout, 30 seconds by default. These tests shorten it to one second, so a wait or a
    // migration that lasts longer shows whether the runner sets the timeout itself.
    [PostgresIntegrationFact]
    public async Task TheWaitForTheMigrationLockOutlastsTheCommandTimeoutOfTheConnection()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = ShortCommandTimeout(database);
        var runner = Runner(dataSource);
        await using var other = await dataSource.OpenConnectionAsync(Token);
        await other.ExecuteAsync(
            "select pg_advisory_lock(@LockId);",
            new { LockId = DatabaseMigrationRunner.MigrationLockId });

        var apply = runner.ApplyAsync([CreateTable], Token);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Token);
        var finishedWhileLocked = apply.IsCompleted;
        await other.ExecuteAsync(
            "select pg_advisory_unlock(@LockId);",
            new { LockId = DatabaseMigrationRunner.MigrationLockId });
        var applied = (await apply).Applied;

        Assert.False(finishedWhileLocked);
        Assert.Equal([CreateTable.Id], applied);
    }

    // The other process queues for the lock while the first migration holds it, and is granted it
    // when that migration commits, so the second migration of the run is the one that waits.
    [PostgresIntegrationFact]
    public async Task TheWaitForTheLockOfALaterMigrationOutlastsTheCommandTimeoutOfTheConnection()
    {
        var slow = new DatabaseMigration("209901010050_slow", "select pg_sleep(0.6);");
        var alter = new DatabaseMigration("209901010200_test_column", "alter table runner_test add column note text;");
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = ShortCommandTimeout(database);
        var runner = Runner(dataSource);
        await runner.ApplyAsync([CreateTable], Token);

        var applied = await ApplyWhileAnotherProcessTakesTheLockAfterTheFirstMigrationAsync(
            dataSource,
            runner,
            [CreateTable, slow, alter]);

        Assert.Equal([slow.Id, alter.Id], applied);
    }

    [PostgresIntegrationFact]
    public async Task TheWaitForTheSessionLockOfAnIndexMigrationOutlastsTheCommandTimeoutOfTheConnection()
    {
        var slow = new DatabaseMigration("209901010050_slow", "select pg_sleep(0.6);");
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = ShortCommandTimeout(database);
        var runner = Runner(dataSource);
        await runner.ApplyAsync([CreateTable], Token);

        var applied = await ApplyWhileAnotherProcessTakesTheLockAfterTheFirstMigrationAsync(
            dataSource,
            runner,
            [CreateTable, slow, ConcurrentIndex]);

        Assert.Equal([slow.Id, ConcurrentIndex.Id], applied);
        Assert.True(await IndexIsValidAsync(dataSource));
    }

    // The SQL of a migration runs under Database:MigrationCommandTimeoutSeconds, not under the
    // connection's default, so a migration that runs longer than 30 seconds is not cut off.
    [PostgresIntegrationFact]
    public async Task AMigrationRunsUnderTheMigrationCommandTimeoutNotTheDefaultOfTheConnection()
    {
        var slow = new DatabaseMigration("209901010050_slow", "select pg_sleep(2.5);");
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = ShortCommandTimeout(database);
        var runner = Runner(dataSource, commandTimeout: TimeSpan.FromSeconds(30));

        var applied = (await runner.ApplyAsync([slow], Token)).Applied;

        Assert.Equal([slow.Id], applied);
        Assert.Equal([slow.Id], await RecordedAsync(dataSource));
    }

    [PostgresIntegrationFact]
    public async Task AMigrationThatRunsLongerThanItsCommandTimeoutFailsNamingTheSettingAndAppliesNothing()
    {
        var slow = new DatabaseMigration(
            "209901010050_slow",
            "create table slow_test (id integer primary key); select pg_sleep(5);");
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var runner = Runner(dataSource, commandTimeout: TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ApplyAsync([slow], Token));

        Assert.Contains(slow.Id, exception.Message, StringComparison.Ordinal);
        Assert.Contains(PlatformOptions.MigrationCommandTimeoutKey, exception.Message, StringComparison.Ordinal);
        Assert.Empty(await RecordedAsync(dataSource));
        await using var connection = await dataSource.OpenConnectionAsync(Token);
        Assert.Null(await connection.ExecuteScalarAsync<string?>("select to_regclass('slow_test')::text;"));
    }

    // A newer release recorded migrations that this build does not contain. The plan reports them,
    // a tolerant run applies what it lacks and reports them, and a run that refuses such a
    // database applies nothing.
    [PostgresIntegrationFact]
    public async Task ARecordedMigrationThatTheBuildDoesNotContainMarksTheDatabaseAhead()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var runner = Runner(dataSource);
        await runner.ApplyAsync([CreateTable], Token);
        await ExecuteAsync(
            dataSource,
            "insert into schema_migrations (id) values ('209902010000_from_the_newer_release'), ('209902010100_and_one_more');");

        var plan = await runner.PlanAsync([CreateTable, ConcurrentIndex], Token);
        var refused = await Assert.ThrowsAsync<DatabaseAheadOfBuildException>(
            () => runner.ApplyUnlessAheadAsync([CreateTable, ConcurrentIndex], Token));
        var recordedAfterRefusal = await RecordedAsync(dataSource);
        var run = await runner.ApplyAsync([CreateTable, ConcurrentIndex], Token);

        Assert.True(plan.IsAhead);
        Assert.Equal([ConcurrentIndex.Id], plan.Pending);
        Assert.Equal(["209902010000_from_the_newer_release", "209902010100_and_one_more"], plan.Unknown);
        Assert.Equal(plan.Unknown, refused.UnknownMigrations);
        Assert.Contains("209902010000_from_the_newer_release", refused.Message, StringComparison.Ordinal);
        Assert.Contains("newer release", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ConcurrentIndex.Id, recordedAfterRefusal);
        Assert.Equal([ConcurrentIndex.Id], run.Applied);
        Assert.Equal(plan.Unknown, run.Unknown);
    }

    [PostgresIntegrationFact]
    public async Task ADatabaseThatRecordedOnlyWhatTheBuildContainsIsNotAhead()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var runner = Runner(dataSource);

        var before = await runner.PlanAsync([CreateTable], Token);
        var run = await runner.ApplyUnlessAheadAsync([CreateTable], Token);
        var after = await runner.PlanAsync([CreateTable], Token);

        Assert.False(before.IsAhead);
        Assert.Equal([CreateTable.Id], run.Applied);
        Assert.Empty(run.Unknown);
        Assert.False(after.IsAhead);
        Assert.Empty(after.Pending);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static NpgsqlDataSource ShortCommandTimeout(PostgresTestDatabaseLease database)
    {
        return NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(database.ConnectionString) { CommandTimeout = 1 }.ConnectionString);
    }

    // Starts a run whose first migration holds the migration lock for a moment, queues another
    // process for the lock behind it, and releases that process after longer than the command
    // timeout of the connection. PostgreSQL grants the lock to the queued process when the
    // migration commits, so the next lock request of the run is the one that waits.
    private static async Task<IReadOnlyList<string>> ApplyWhileAnotherProcessTakesTheLockAfterTheFirstMigrationAsync(
        NpgsqlDataSource dataSource,
        DatabaseMigrationRunner runner,
        IReadOnlyList<DatabaseMigration> migrations)
    {
        await using var other = await dataSource.OpenConnectionAsync(Token);
        var apply = runner.ApplyAsync(migrations, Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        await other.ExecuteAsync(
            "select pg_advisory_lock(@LockId);",
            new { LockId = DatabaseMigrationRunner.MigrationLockId },
            commandTimeout: 0);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Token);
        var finishedWhileLocked = apply.IsCompleted;
        await other.ExecuteAsync(
            "select pg_advisory_unlock(@LockId);",
            new { LockId = DatabaseMigrationRunner.MigrationLockId });
        var run = await apply;

        Assert.False(finishedWhileLocked);
        return run.Applied;
    }

    private static DatabaseMigrationRunner Runner(
        NpgsqlDataSource dataSource,
        TimeSpan? lockTimeout = null,
        TimeSpan? commandTimeout = null)
    {
        var options = new PlatformOptions(
            dataSource.ConnectionString,
            "127.0.0.1:6379",
            TimeSpan.FromSeconds(1),
            RunMigrationsOnStartup: true)
        {
            MigrationLockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5),
            MigrationCommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(300)
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
