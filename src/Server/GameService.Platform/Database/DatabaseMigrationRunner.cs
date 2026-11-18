using Dapper;
using Mmorpg.GameService.Platform.Configuration;
using Npgsql;

namespace Mmorpg.GameService.Platform.Database;

// Applies migrations in order under one advisory lock, so concurrent GameService processes
// apply each exactly once. A migration in a transaction holds the lock for its transaction,
// waits for table locks only up to the migration lock timeout, and runs its SQL under the
// migration command timeout. A migration outside a transaction runs alone under the
// session-level form of the same lock. Waiting for the lock itself never times out.
public sealed class DatabaseMigrationRunner(
    NpgsqlDataSource dataSource,
    PlatformOptions options,
    ILogger<DatabaseMigrationRunner> logger)
{
    public const long MigrationLockId = 7_104_202_607_071_600;

    // Npgsql's command timeout of 30 seconds would end a wait that never times out: the wait for
    // the migration lock lasts as long as another process migrates, and a concurrent index build
    // lasts as long as the table needs.
    private const int NoCommandTimeout = 0;

    // The lock of one whole migrate-and-seed. The migration lock above serializes single
    // migrations; this one serializes the complete operation of a process, from its read of the
    // recorded ids to its last seed, so what one process decided about the database still holds
    // when it seeds. It is a session lock on a connection of its own, so the migrations and the
    // seeders, which use other connections, never wait for it.
    public const long OperationLockId = 7_104_202_611_180_900;

    // Waits for the operation lock, as long as another process holds it, and holds it until the
    // result is disposed.
    public async Task<IAsyncDisposable> HoldOperationLockAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "select pg_advisory_lock(@OperationLockId);",
                new { OperationLockId },
                commandTimeout: NoCommandTimeout,
                cancellationToken: cancellationToken));
            return new OperationLock(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class OperationLock(NpgsqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // The connection goes back to the pool without the lock.
                await connection.ExecuteAsync("select pg_advisory_unlock(@OperationLockId);", new { OperationLockId });
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
            {
                // A broken session has lost the lock already.
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    // The ids of the migrations that the database has not recorded, in order, and the recorded
    // ids that these migrations do not contain, which a newer release recorded. Reads only
    // schema_migrations, and a database without it has recorded none.
    public async Task<MigrationPlan> PlanAsync(
        IEnumerable<DatabaseMigration> migrations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var recorded = await ReadAppliedAsync(connection, cancellationToken);
        return Compare(migrations, recorded).Plan;
    }

    // Applies every migration that the database has not recorded and says which it applied. A
    // database that recorded all of them sees only the read of their ids. A database that a newer
    // release migrated gets the migrations that it lacks all the same: the recorded ids that these
    // migrations do not contain are only reported.
    public Task<MigrationRun> ApplyAsync(
        IEnumerable<DatabaseMigration> migrations,
        CancellationToken cancellationToken)
    {
        return ApplyAsync(migrations, refuseWhenAhead: false, cancellationToken);
    }

    // Like ApplyAsync, but a database that a newer release migrated is refused before anything is
    // applied, with a DatabaseAheadOfBuildException: this build cannot know what the newer
    // release changed, so it must neither migrate nor seed over it.
    public Task<MigrationRun> ApplyUnlessAheadAsync(
        IEnumerable<DatabaseMigration> migrations,
        CancellationToken cancellationToken)
    {
        return ApplyAsync(migrations, refuseWhenAhead: true, cancellationToken);
    }

    private async Task<MigrationRun> ApplyAsync(
        IEnumerable<DatabaseMigration> migrations,
        bool refuseWhenAhead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var recorded = await ReadAppliedAsync(connection, cancellationToken);
        var (plan, pending) = Compare(migrations, recorded);
        if (refuseWhenAhead && plan.IsAhead)
        {
            throw new DatabaseAheadOfBuildException(plan.Unknown);
        }

        if (pending.Length == 0)
        {
            return new MigrationRun([], plan.Unknown);
        }

        await EnsureMigrationTableAsync(connection, cancellationToken);
        var applied = new List<string>();
        foreach (var migration in pending)
        {
            var ran = migration.RunsOutsideTransaction
                ? await ApplyOutsideTransactionAsync(connection, migration, cancellationToken)
                : await ApplyInTransactionAsync(connection, migration, cancellationToken);
            if (ran)
            {
                applied.Add(migration.Id);
            }
        }

        return new MigrationRun(applied, plan.Unknown);
    }

    private static (MigrationPlan Plan, DatabaseMigration[] Pending) Compare(
        IEnumerable<DatabaseMigration> migrations,
        HashSet<string> recorded)
    {
        var all = migrations.ToArray();
        var pending = all.Where(migration => !recorded.Contains(migration.Id)).ToArray();
        var known = all.Select(migration => migration.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = recorded.Where(id => !known.Contains(id)).Order(StringComparer.Ordinal).ToArray();
        return (new MigrationPlan(pending.Select(migration => migration.Id).ToArray(), unknown), pending);
    }

    private static async Task<HashSet<string>> ReadAppliedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await PostgresCatalog.TableExistsAsync(connection, "schema_migrations", cancellationToken))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var ids = await connection.QueryAsync<string>(new CommandDefinition(
            "select id from schema_migrations;",
            cancellationToken: cancellationToken));
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    private static async Task EnsureMigrationTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "select pg_advisory_xact_lock(@MigrationLockId);",
                new { MigrationLockId },
                transaction,
                commandTimeout: NoCommandTimeout,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                create table if not exists schema_migrations (
                    id text primary key,
                    applied_at timestamptz not null default now()
                );
                """,
                transaction: transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<bool> ApplyInTransactionAsync(
        NpgsqlConnection connection,
        DatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "select pg_advisory_xact_lock(@MigrationLockId);",
                new { MigrationLockId },
                transaction: transaction,
                commandTimeout: NoCommandTimeout,
                cancellationToken: cancellationToken));

            // Set after the advisory lock, so waiting for another migrating process never
            // times out; only a table lock that live traffic holds does.
            await connection.ExecuteAsync(new CommandDefinition(
                "select set_config('lock_timeout', @LockTimeout, true);",
                new { LockTimeout = LockTimeoutSetting() },
                transaction,
                cancellationToken: cancellationToken));

            if (await IsRecordedAsync(connection, transaction, migration, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            await RunSqlAsync(connection, transaction, migration, cancellationToken);
            await RecordAsync(connection, transaction, migration, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Applied database migration {MigrationId}.", migration.Id);
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Database migration {migration.Id} waited longer than "
                + $"{options.MigrationLockTimeout.TotalMilliseconds:0} ms for a lock that other sessions hold, "
                + "so nothing of it was applied. Run it again when that traffic has passed, or raise "
                + $"{PlatformOptions.MigrationLockTimeoutKey}.",
                exception);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // Each statement commits on its own, so the session-level lock keeps other migrating
    // processes out until the migration is recorded. No lock timeout applies: a concurrent
    // build blocks no traffic while it waits.
    private async Task<bool> ApplyOutsideTransactionAsync(
        NpgsqlConnection connection,
        DatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_lock(@MigrationLockId);",
            new { MigrationLockId },
            commandTimeout: NoCommandTimeout,
            cancellationToken: cancellationToken));

        bool applied;
        try
        {
            applied = await ApplyLockedOutsideTransactionAsync(connection, migration, cancellationToken);
        }
        catch
        {
            await TryUnlockAsync(connection);
            throw;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_unlock(@MigrationLockId);",
            new { MigrationLockId },
            cancellationToken: cancellationToken));
        return applied;
    }

    private async Task<bool> ApplyLockedOutsideTransactionAsync(
        NpgsqlConnection connection,
        DatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        if (await IsRecordedAsync(connection, null, migration, cancellationToken))
        {
            return false;
        }

        var indexName = migration.ConcurrentIndex!;
        var indexIsValid = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            """
            select entry.indisvalid
            from pg_index entry
            where entry.indexrelid = to_regclass(@IndexName);
            """,
            new { IndexName = indexName },
            cancellationToken: cancellationToken));
        if (indexIsValid == false)
        {
            // An earlier build failed and left the index invalid. The name is a checked
            // identifier, never input.
            logger.LogWarning(
                "Dropping the invalid index {IndexName} that an earlier run of database migration {MigrationId} left behind.",
                indexName,
                migration.Id);
            // Like the build, the drop waits for the transactions that use the table and
            // blocks none of them, so it gets no command timeout either.
            await connection.ExecuteAsync(new CommandDefinition(
                $"drop index concurrently if exists {indexName};",
                commandTimeout: 0,
                cancellationToken: cancellationToken));
        }

        // A valid index means an earlier run built it but stopped before recording it.
        if (indexIsValid != true)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                migration.Sql,
                commandTimeout: 0,
                cancellationToken: cancellationToken));
        }

        await RecordAsync(connection, null, migration, cancellationToken);
        logger.LogInformation(
            "Applied database migration {MigrationId} outside a transaction.",
            migration.Id);
        return true;
    }

    // The SQL of a migration in a transaction runs under the explicit migration command timeout,
    // not Npgsql's 30 seconds, which would fail every migration that runs longer.
    private async Task RunSqlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                migration.Sql,
                transaction: transaction,
                commandTimeout: (int)Math.Ceiling(options.MigrationCommandTimeout.TotalSeconds),
                cancellationToken: cancellationToken));
        }
        catch (NpgsqlException exception) when (
            exception.InnerException is TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Database migration {migration.Id} ran longer than "
                + $"{options.MigrationCommandTimeout.TotalSeconds:0} s and was cancelled, so nothing of it was applied. "
                + $"Raise {PlatformOptions.MigrationCommandTimeoutKey} to run it.",
                exception);
        }
    }

    private static Task<bool> IsRecordedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from schema_migrations where id = @MigrationId);",
            new { MigrationId = migration.Id },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            "insert into schema_migrations (id) values (@MigrationId);",
            new { MigrationId = migration.Id },
            transaction,
            cancellationToken: cancellationToken));
    }

    // A broken connection ends its session and with it the lock, so a failed unlock only
    // logs.
    private async Task TryUnlockAsync(NpgsqlConnection connection)
    {
        try
        {
            await connection.ExecuteAsync("select pg_advisory_unlock(@MigrationLockId);", new { MigrationLockId });
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            logger.LogWarning(
                exception,
                "Could not release the migration lock after a failed migration; the session ends with the connection.");
        }
    }

    private string LockTimeoutSetting()
    {
        return $"{(long)options.MigrationLockTimeout.TotalMilliseconds}ms";
    }
}
