using Dapper;
using Npgsql;

namespace Mmorpg.GameService.Platform.Database;

// Applies migrations in order. Each migration runs in its own transaction under one
// advisory lock, so concurrent GameService instances apply it exactly once.
public sealed class DatabaseMigrationRunner(
    NpgsqlDataSource dataSource,
    ILogger<DatabaseMigrationRunner> logger)
{
    public const long MigrationLockId = 7_104_202_607_071_600;

    public async Task ApplyAsync(
        IEnumerable<DatabaseMigration> migrations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureMigrationTableAsync(connection, cancellationToken);

        foreach (var migration in migrations)
        {
            await ApplyMigrationAsync(connection, migration, cancellationToken);
        }
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

    private async Task ApplyMigrationAsync(
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
                cancellationToken: cancellationToken));

            var alreadyApplied = await connection.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "select exists (select 1 from schema_migrations where id = @MigrationId);",
                    new { MigrationId = migration.Id },
                    transaction,
                    cancellationToken: cancellationToken));

            if (alreadyApplied)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                migration.Sql,
                transaction: transaction,
                cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(
                "insert into schema_migrations (id) values (@MigrationId);",
                new { MigrationId = migration.Id },
                transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Applied database migration {MigrationId}.", migration.Id);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
