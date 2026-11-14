using System.Globalization;
using Npgsql;

namespace Mmorpg.Tools.StackStressGenerator;

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
    long ExpectedLootQuantity,
    long ActualLootQuantity,
    long ExpectedInventoryItemQuantity,
    long ActualInventoryItemQuantity,
    int DuplicateContainerSlots,
    int InvalidItemCustodyRows,
    int PendingItemOperations)
{
    public int Violations =>
        (ExpectedLootQuantity == ActualLootQuantity ? 0 : 1)
        + (ExpectedInventoryItemQuantity == ActualInventoryItemQuantity ? 0 : 1)
        + DuplicateContainerSlots
        + InvalidItemCustodyRows
        + PendingItemOperations;
}

// The read-only sampler of the disposable database. Once a second it reads the database
// statistics and the simulation sessions; every report interval it runs the drift checks; at
// both ends of the steady interval it counts every table; after the run it checks the item
// invariants. It keeps the first and last sample and running extremes, not the samples, so a
// long run does not grow the generator's memory.
public sealed class StressPostgresSampler : IAsyncDisposable
{
    // The application name of the sampler's own connections, which the drift checks leave out
    // of the connections GameService uses.
    public const string ApplicationName = "stack-stress-sampler";

    // A simulation session is renewed every 10 seconds, SimulationWorker's
    // SimulationSessionHeartbeatSeconds, so one with less lease left missed its last renewal.
    public const double LeaseExpiryThresholdSeconds = 10d;

    // Counting every table must stay cheap; a statement that takes longer fails the run instead
    // of holding it.
    private const int TableCountTimeoutSeconds = 60;

    private readonly NpgsqlDataSource dataSource;
    private readonly string expectedDatabase;
    private readonly string workerId;
    private readonly StressDriftAccumulator steadyDrift;
    private readonly object driftGate = new();
    private PostgresSample? first;
    private PostgresSample? last;
    private int samples;
    private int maximumConnections;
    private int maximumActiveConnections;
    private int maximumWaitingConnections;
    private int maximumIdleInTransactionConnections;
    private int maximumActiveSessions;
    private int maximumExpiredUnreleasedSessions;
    private double? minimumLeaseHeadroomMilliseconds;
    private bool steady;
    private StressItemInvariantSummary? itemInvariants;

    private StressPostgresSampler(
        NpgsqlDataSource dataSource,
        string expectedDatabase,
        string workerId,
        double? workerHeartbeatTimeoutSeconds,
        int? databasePoolSize)
    {
        this.dataSource = dataSource;
        this.expectedDatabase = expectedDatabase;
        this.workerId = workerId;
        steadyDrift = new StressDriftAccumulator(LeaseExpiryThresholdSeconds, workerHeartbeatTimeoutSeconds, databasePoolSize);
    }

    public static StressPostgresSampler Create(
        string connectionString,
        string confirmedDisposableDatabase,
        string workerId = "local-simulation-worker-2",
        double? workerHeartbeatTimeoutSeconds = null,
        int? databasePoolSize = null)
    {
        var builder = ValidateConnectionString(
            connectionString,
            confirmedDisposableDatabase);
        builder.ApplicationName = ApplicationName;
        return new StressPostgresSampler(
            NpgsqlDataSource.Create(builder.ConnectionString),
            builder.Database!,
            workerId,
            workerHeartbeatTimeoutSeconds,
            databasePoolSize);
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
                "The disposable database has not been initialized by GameService.");
        }

        await using var accountsCommand = connection.CreateCommand();
        accountsCommand.CommandText = "select count(*) from accounts;";
        var accountCount = Convert.ToInt64(
            await accountsCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
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

        Add(new PostgresSample(
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

    // The drift checks: the sessions against the bots the generator holds joined, the sessions
    // close to the end of their lease, the age of the worker's registry heartbeat, the pending
    // item operations, the connections GameService holds and uses, and the oldest open
    // transaction that waits idle. Within the steady interval the worst of each is kept.
    public async Task<StressDriftSample> SampleDriftAsync(int joinedBots, CancellationToken cancellationToken)
    {
        const string sql = """
            select (select count(*)::integer
                    from character_simulation_sessions s
                    where s.released_at is null
                      and s.expires_at > now()) as active_sessions,
                   (select count(*)::integer
                    from character_simulation_sessions s
                    where s.released_at is null
                      and s.expires_at > now()
                      and s.expires_at <= now() + make_interval(secs => @Threshold)) as near_expiry,
                   (select min(extract(epoch from (s.expires_at - now())))::double precision
                    from character_simulation_sessions s
                    where s.released_at is null
                      and s.expires_at > now()) as lease_headroom_seconds,
                   (select extract(epoch from (now() - w.last_heartbeat_at))::double precision
                    from simulation_workers w
                    where w.id = @WorkerId) as worker_heartbeat_age_seconds,
                   (select count(*)::integer
                    from item_operations o
                    where o.status = 'pending') as pending_item_operations,
                   (select count(*)::integer
                    from pg_stat_activity a
                    where a.datname = current_database()
                      and a.backend_type = 'client backend'
                      and a.application_name is distinct from @Sampler) as service_connections,
                   (select count(*)::integer
                    from pg_stat_activity a
                    where a.datname = current_database()
                      and a.backend_type = 'client backend'
                      and a.application_name is distinct from @Sampler
                      and a.state is distinct from 'idle') as service_connections_in_use,
                   (select coalesce(max(extract(epoch from (now() - a.state_change))), 0)::double precision
                    from pg_stat_activity a
                    where a.datname = current_database()
                      and a.state in ('idle in transaction', 'idle in transaction (aborted)')) as oldest_idle_in_transaction_seconds;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("Threshold", LeaseExpiryThresholdSeconds);
        command.Parameters.AddWithValue("WorkerId", workerId);
        command.Parameters.AddWithValue("Sampler", ApplicationName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("PostgreSQL did not return the drift checks.");
        }

        var sample = new StressDriftSample(
            DateTime.UtcNow,
            joinedBots,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetDouble(2),
            reader.IsDBNull(3) ? null : reader.GetDouble(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetDouble(7));
        lock (driftGate)
        {
            if (steady)
            {
                steadyDrift.Add(sample);
            }
        }

        return sample;
    }

    // Every user table's exact row count and total size. The disposable database of a run
    // holds at most a few hundred thousand rows per table, so an exact count is cheap, unlike the
    // estimate of the statistics, which only an ANALYZE would make exact.
    public async Task<StressTableSnapshot> CountTablesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   (xpath('/row/count/text()',
                          query_to_xml(format('select count(*) from %I.%I', n.nspname, c.relname), false, true, '')))[1]::text::bigint,
                   pg_total_relation_size(c.oid)
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p')
              and n.nspname not in ('pg_catalog', 'information_schema')
              and n.nspname not like 'pg_toast%'
            order by 1;
            """;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = TableCountTimeoutSeconds;
        var tables = new List<StressTableSize>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                tables.Add(new StressTableSize(
                    name.StartsWith("public.", StringComparison.Ordinal) ? name["public.".Length..] : name,
                    reader.GetInt64(1),
                    reader.GetInt64(2)));
            }
        }

        return new StressTableSnapshot(
            DateTime.UtcNow,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            tables);
    }

    // What the database says about a body that is gone: when its session was released, the
    // character's latest death, and the saved state. PostgreSQL's clock is read in the same
    // statement, so every time comes back on the generator's clock.
    public async Task<PlayingRejoinFacts> ReadRejoinFactsAsync(
        Guid characterId,
        Guid simulationSessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select now(),
                   (select s.released_at from character_simulation_sessions s where s.id = @Session),
                   (select max(d.committed_at) from death_events d where d.character_id = @Character),
                   st.position_x,
                   st.position_y,
                   st.position_z,
                   st.health,
                   st.mana,
                   st.simulation_session_id
            from (select 1) one
            left join character_simulation_states st on st.character_id = @Character;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("Session", simulationSessionId);
        command.Parameters.AddWithValue("Character", characterId);
        var before = DateTime.UtcNow;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var after = DateTime.UtcNow;
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("PostgreSQL did not return the facts of a rejoin.");
        }

        var offset = (before + ((after - before) / 2)) - ToUtc(reader.GetDateTime(0));
        DateTime? Local(int ordinal) => reader.IsDBNull(ordinal) ? null : ToUtc(reader.GetDateTime(ordinal)) + offset;
        var saved = reader.IsDBNull(3)
            ? null
            : new PlayingSavedState(
                new Mmorpg.GameSimulation.SimulationVector3(reader.GetFloat(3), reader.GetFloat(4), reader.GetFloat(5)),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetGuid(8));
        return new PlayingRejoinFacts(Local(1), Local(2), saved);
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
            CultureInfo.InvariantCulture);
        if (actualAccountCount != expectedAccountCount)
        {
            throw new InvalidOperationException(
                $"The observed PostgreSQL database contains {actualAccountCount} accounts after the run, expected {expectedAccountCount}. GameService may be using a different database.");
        }
    }

    public async Task VerifyGameplayInvariantsAsync(
        StressWorkloadProfile workload,
        int inventoryFixtureBots,
        bool hasLootHotspot,
        StressFixtureDefinitions? fixtureDefinitions,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                coalesce(sum(item.quantity) filter (
                    where item.definition_id = @LootDefinitionId), 0)::bigint,
                coalesce(sum(item.quantity) filter (
                    where item.definition_id = @InventoryDefinitionId), 0)::bigint,
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
        command.Parameters.AddWithValue(
            "LootDefinitionId",
            fixtureDefinitions?.LootDefinitionId ?? string.Empty);
        command.Parameters.AddWithValue(
            "InventoryDefinitionId",
            fixtureDefinitions?.InventoryDefinitionId ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "PostgreSQL did not return stack stress item invariants.");
        }

        var expectedLoot = workload is StressWorkloadProfile.LootHotspot
            or StressWorkloadProfile.MixedGameplay
                ? inventoryFixtureBots + (hasLootHotspot ? 24L * 50L : 0L)
                : 0L;
        var expectedInventoryItem = workload is StressWorkloadProfile.Inventory
            or StressWorkloadProfile.MixedGameplay
                ? inventoryFixtureBots
                : 0L;
        itemInvariants = new StressItemInvariantSummary(
            expectedLoot,
            reader.GetInt64(0),
            expectedInventoryItem,
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    // The drift checks keep their worst values from here until MarkSteadyEnd.
    public void MarkSteadyState()
    {
        lock (driftGate)
        {
            steady = true;
        }
    }

    public void MarkSteadyEnd()
    {
        lock (driftGate)
        {
            steady = false;
        }
    }

    public StressDriftSummary? CaptureDrift()
    {
        lock (driftGate)
        {
            return steadyDrift.Capture();
        }
    }

    public StressPostgresSummary? CreateSummary()
    {
        if (samples < 2 || first is null || last is null)
        {
            return null;
        }

        return new StressPostgresSummary(
            last.Database,
            samples,
            first.Connections,
            maximumConnections,
            last.Connections,
            maximumActiveConnections,
            maximumWaitingConnections,
            maximumIdleInTransactionConnections,
            maximumActiveSessions,
            maximumExpiredUnreleasedSessions,
            minimumLeaseHeadroomMilliseconds ?? 0d,
            NonNegativeDelta(last.SimulationSessionRowsInserted, first.SimulationSessionRowsInserted),
            NonNegativeDelta(last.SimulationSessionRowsUpdated, first.SimulationSessionRowsUpdated),
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

    private void Add(PostgresSample sample)
    {
        first ??= sample;
        last = sample;
        samples++;
        maximumConnections = Math.Max(maximumConnections, sample.Connections);
        maximumActiveConnections = Math.Max(maximumActiveConnections, sample.ActiveConnections);
        maximumWaitingConnections = Math.Max(maximumWaitingConnections, sample.WaitingConnections);
        maximumIdleInTransactionConnections = Math.Max(maximumIdleInTransactionConnections, sample.IdleInTransactionConnections);
        maximumActiveSessions = Math.Max(maximumActiveSessions, sample.ActiveSimulationSessions);
        maximumExpiredUnreleasedSessions = Math.Max(maximumExpiredUnreleasedSessions, sample.ExpiredUnreleasedSimulationSessions);
        if (sample.ActiveSimulationSessions > 0)
        {
            minimumLeaseHeadroomMilliseconds = Math.Min(
                minimumLeaseHeadroomMilliseconds ?? double.MaxValue,
                sample.MinimumSessionLeaseHeadroomMilliseconds);
        }
    }

    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
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
