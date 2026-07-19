using Npgsql;

namespace ShooterMmo.Tools.StackStressGenerator;

public sealed record StressPostgresSummary(
    string Database,
    int Samples,
    int InitialConnections,
    int MaximumConnections,
    int FinalConnections,
    int MaximumActiveConnections,
    int MaximumWaitingConnections,
    int MaximumIdleInTransactionConnections,
    int MaximumActiveSimulationSessions,
    int MaximumExpiredUnreleasedSimulationSessions,
    double MinimumActiveSessionLeaseHeadroomMilliseconds,
    long SimulationSessionRowsInserted,
    long SimulationSessionRowsUpdated,
    long CommittedTransactions,
    long RolledBackTransactions,
    long RowsInserted,
    long RowsUpdated,
    long RowsDeleted,
    long TemporaryFiles,
    long TemporaryBytes,
    long Deadlocks,
    double BlockReadMilliseconds,
    double BlockWriteMilliseconds,
    StressItemInvariantSummary? ItemInvariants);

public sealed record StressItemInvariantSummary(
    long ExpectedIronOreQuantity,
    long ActualIronOreQuantity,
    long ExpectedFieldDressingQuantity,
    long ActualFieldDressingQuantity,
    int DuplicateContainerSlots,
    int InvalidItemCustodyRows,
    int PendingItemOperations)
{
    public int Violations =>
        (ExpectedIronOreQuantity == ActualIronOreQuantity ? 0 : 1)
        + (ExpectedFieldDressingQuantity == ActualFieldDressingQuantity ? 0 : 1)
        + DuplicateContainerSlots
        + InvalidItemCustodyRows
        + PendingItemOperations;
}

public sealed class StressPostgresSampler : IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string expectedDatabase;
    private readonly List<PostgresSample> samples = [];
    private StressItemInvariantSummary? itemInvariants;

    private StressPostgresSampler(
        NpgsqlDataSource dataSource,
        string expectedDatabase)
    {
        this.dataSource = dataSource;
        this.expectedDatabase = expectedDatabase;
    }

    public static StressPostgresSampler Create(
        string connectionString,
        string confirmedDisposableDatabase)
    {
        var builder = ValidateConnectionString(
            connectionString,
            confirmedDisposableDatabase);
        return new StressPostgresSampler(
            NpgsqlDataSource.Create(builder.ConnectionString),
            builder.Database!);
    }

    public static NpgsqlConnectionStringBuilder ValidateConnectionString(
        string connectionString,
        string confirmedDisposableDatabase)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new StressGeneratorOptionException(
                "The full-stack PostgreSQL connection string cannot be empty.");
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new StressGeneratorOptionException(
                $"The full-stack PostgreSQL connection string is invalid: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(builder.Database)
            || string.IsNullOrWhiteSpace(builder.Host))
        {
            throw new StressGeneratorOptionException(
                "The full-stack PostgreSQL connection string must include host and database.");
        }

        var host = builder.Host.Trim();
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            && !string.Equals(host, "::1", StringComparison.Ordinal))
        {
            throw new StressGeneratorOptionException(
                "Full-stack PostgreSQL must use a loopback host.");
        }

        var database = builder.Database.Trim();
        if (!database.Contains("stress", StringComparison.OrdinalIgnoreCase)
            && !database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new StressGeneratorOptionException(
                "The disposable PostgreSQL database name must contain 'stress' or 'test'.");
        }

        if (!string.Equals(
                database,
                confirmedDisposableDatabase?.Trim(),
                StringComparison.Ordinal))
        {
            throw new StressGeneratorOptionException(
                "--confirm-disposable-database must exactly match the PostgreSQL database name.");
        }

        return builder;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var databaseCommand = connection.CreateCommand();
        databaseCommand.CommandText = "select current_database();";
        var actualDatabase = (string?)await databaseCommand.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(actualDatabase, expectedDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"PostgreSQL connected to '{actualDatabase}', expected disposable database '{expectedDatabase}'.");
        }

        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "select to_regclass('public.accounts')::text;";
        var accountsTable = (string?)await tableCommand.ExecuteScalarAsync(cancellationToken);
        if (accountsTable is null)
        {
            throw new InvalidOperationException(
                "The disposable database has not been initialized by AuthService.");
        }

        await using var accountsCommand = connection.CreateCommand();
        accountsCommand.CommandText = "select count(*) from accounts;";
        var accountCount = Convert.ToInt64(
            await accountsCommand.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (accountCount != 0)
        {
            throw new InvalidOperationException(
                $"The disposable database already contains {accountCount} accounts. Recreate the stress stack before running.");
        }

        await SampleAsync(cancellationToken);
    }

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select d.datname,
                   d.numbackends::integer,
                   d.xact_commit,
                   d.xact_rollback,
                   d.tup_inserted,
                   d.tup_updated,
                   d.tup_deleted,
                   d.temp_files,
                   d.temp_bytes,
                   d.deadlocks,
                   d.blk_read_time,
                   d.blk_write_time,
                   (select count(*)::integer
                    from pg_stat_activity a
                    where a.datname = current_database()
                      and a.state = 'active') as active_connections,
                   (select count(*)::integer
                    from pg_stat_activity a
                    where a.datname = current_database()
                      and a.state = 'active'
                      and a.wait_event is not null) as waiting_connections,
                   (select count(*)::integer
                    from pg_stat_activity a
                    where a.datname = current_database()
                      and a.state = 'idle in transaction') as idle_in_transaction_connections,
                   (select count(*)::integer
                    from character_simulation_sessions s
                    where s.released_at is null
                      and s.expires_at > now()) as active_simulation_sessions,
                   (select count(*)::integer
                    from character_simulation_sessions s
                    where s.released_at is null
                      and s.expires_at <= now()) as expired_unreleased_simulation_sessions,
                   (select coalesce(
                               min(extract(epoch from (s.expires_at - now())) * 1000.0),
                               0.0)::double precision
                    from character_simulation_sessions s
                    where s.released_at is null
                      and s.expires_at > now()) as minimum_session_lease_headroom_ms,
                   (select coalesce(t.n_tup_ins, 0)
                    from pg_stat_user_tables t
                    where t.relname = 'character_simulation_sessions') as simulation_session_rows_inserted,
                   (select coalesce(t.n_tup_upd, 0)
                    from pg_stat_user_tables t
                    where t.relname = 'character_simulation_sessions') as simulation_session_rows_updated
            from pg_stat_database d
            where d.datname = current_database();
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "PostgreSQL did not return statistics for the disposable database.");
        }

        samples.Add(new PostgresSample(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetDouble(10),
            reader.GetDouble(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetInt32(16),
            reader.GetDouble(17),
            reader.GetInt64(18),
            reader.GetInt64(19)));
    }

    public async Task VerifyAccountCountAsync(
        int expectedAccountCount,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from accounts;";
        var actualAccountCount = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (actualAccountCount != expectedAccountCount)
        {
            throw new InvalidOperationException(
                $"The observed PostgreSQL database contains {actualAccountCount} accounts after the run, expected {expectedAccountCount}. AuthService may be using a different database.");
        }
    }

    public async Task VerifyGameplayInvariantsAsync(
        StressWorkloadProfile workload,
        int inventoryFixtureBots,
        bool hasLootHotspot,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                coalesce(sum(item.quantity) filter (
                    where item.definition_id = 'material.iron_ore'), 0)::bigint,
                coalesce(sum(item.quantity) filter (
                    where item.definition_id = 'medical.field_dressing'), 0)::bigint,
                (select count(*)::integer
                 from (
                     select duplicate.container_id, duplicate.container_slot_index
                     from item_instances duplicate
                     where duplicate.container_id is not null
                     group by duplicate.container_id, duplicate.container_slot_index
                     having count(*) > 1
                 ) duplicate_slots),
                count(*) filter (
                    where not (
                        (item.container_id is not null
                         and item.container_slot_index is not null
                         and item.equipped_character_id is null
                         and item.equipment_slot_id is null)
                        or
                        (item.container_id is null
                         and item.container_slot_index is null
                         and item.equipped_character_id is not null
                         and item.equipment_slot_id is not null)))::integer,
                (select count(*)::integer
                 from item_operations operation
                 where operation.status = 'pending')
            from item_instances item;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "PostgreSQL did not return stack stress item invariants.");
        }

        var expectedIron = workload is StressWorkloadProfile.LootHotspot
            or StressWorkloadProfile.MixedGameplay
                ? inventoryFixtureBots + (hasLootHotspot ? 24L * 50L : 0L)
                : 0L;
        var expectedDressing = workload is StressWorkloadProfile.Inventory
            or StressWorkloadProfile.MixedGameplay
                ? inventoryFixtureBots
                : 0L;
        itemInvariants = new StressItemInvariantSummary(
            expectedIron,
            reader.GetInt64(0),
            expectedDressing,
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    public StressPostgresSummary? CreateSummary()
    {
        if (samples.Count < 2)
        {
            return null;
        }

        var first = samples[0];
        var last = samples[^1];
        var activeSessionSamples = samples
            .Where(sample => sample.ActiveSimulationSessions > 0)
            .ToArray();
        return new StressPostgresSummary(
            last.Database,
            samples.Count,
            first.Connections,
            samples.Max(sample => sample.Connections),
            last.Connections,
            samples.Max(sample => sample.ActiveConnections),
            samples.Max(sample => sample.WaitingConnections),
            samples.Max(sample => sample.IdleInTransactionConnections),
            samples.Max(sample => sample.ActiveSimulationSessions),
            samples.Max(sample => sample.ExpiredUnreleasedSimulationSessions),
            activeSessionSamples.Length == 0
                ? 0d
                : activeSessionSamples.Min(sample =>
                    sample.MinimumSessionLeaseHeadroomMilliseconds),
            NonNegativeDelta(
                last.SimulationSessionRowsInserted,
                first.SimulationSessionRowsInserted),
            NonNegativeDelta(
                last.SimulationSessionRowsUpdated,
                first.SimulationSessionRowsUpdated),
            NonNegativeDelta(last.CommittedTransactions, first.CommittedTransactions),
            NonNegativeDelta(last.RolledBackTransactions, first.RolledBackTransactions),
            NonNegativeDelta(last.RowsInserted, first.RowsInserted),
            NonNegativeDelta(last.RowsUpdated, first.RowsUpdated),
            NonNegativeDelta(last.RowsDeleted, first.RowsDeleted),
            NonNegativeDelta(last.TemporaryFiles, first.TemporaryFiles),
            NonNegativeDelta(last.TemporaryBytes, first.TemporaryBytes),
            NonNegativeDelta(last.Deadlocks, first.Deadlocks),
            NonNegativeDelta(last.BlockReadMilliseconds, first.BlockReadMilliseconds),
            NonNegativeDelta(last.BlockWriteMilliseconds, first.BlockWriteMilliseconds),
            itemInvariants);
    }

    public async ValueTask DisposeAsync()
    {
        await dataSource.DisposeAsync();
    }

    private static long NonNegativeDelta(long final, long initial)
    {
        return Math.Max(0L, final - initial);
    }

    private static double NonNegativeDelta(double final, double initial)
    {
        return Math.Max(0d, final - initial);
    }

    private sealed record PostgresSample(
        string Database,
        int Connections,
        long CommittedTransactions,
        long RolledBackTransactions,
        long RowsInserted,
        long RowsUpdated,
        long RowsDeleted,
        long TemporaryFiles,
        long TemporaryBytes,
        long Deadlocks,
        double BlockReadMilliseconds,
        double BlockWriteMilliseconds,
        int ActiveConnections,
        int WaitingConnections,
        int IdleInTransactionConnections,
        int ActiveSimulationSessions,
        int ExpiredUnreleasedSimulationSessions,
        double MinimumSessionLeaseHeadroomMilliseconds,
        long SimulationSessionRowsInserted,
        long SimulationSessionRowsUpdated);
}
