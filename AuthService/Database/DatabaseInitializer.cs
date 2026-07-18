using AuthService.Database.Migrations;
using AuthService.Items;
using Dapper;
using Npgsql;

namespace AuthService.Database;

public sealed class DatabaseInitializer(
    NpgsqlDataSource dataSource,
    ItemCatalogSeeder itemCatalogSeeder,
    CharacterItemStateBootstrapper characterItemStateBootstrapper,
    ILogger<DatabaseInitializer> logger)
{
    private const long MigrationLockId = 7_104_202_607_071_600;

    private static readonly IReadOnlyCollection<DatabaseMigration> Migrations =
    [
        new("202607071600_auth_character_world_join", LegacyInitialAuthSchemaMigrationSql),
        new("202607101200_character_world_sessions", LegacySimulationSessionLeaseMigrationSql),
        new("202607121200_session_ticket_ownership", LegacyTicketOwnershipMigrationSql),
        new("202607121500_world_session_ownership", LegacySessionOwnershipMigrationSql),
        new("202607131000_world_registry_heartbeat", LegacyRegistryHeartbeatMigrationSql),
        new("202607131900_single_active_account_session", LegacySingleActiveAccountSessionMigrationSql),
        new("202607132100_world_runtime_registration", LegacyRuntimeRegistrationMigrationSql),
        new("202607141200_simulation_topology", SimulationTopologyMigrationSql),
        new(
            "202607141300_single_active_account_simulation_session",
            SingleActiveAccountSimulationSessionMigrationSql),
        new(ItemPersistenceFoundationMigration.Id, ItemPersistenceFoundationMigration.Sql),
        new(PlayerCorpsePersistenceMigration.Id, PlayerCorpsePersistenceMigration.Sql),
        new(PlayerDeathCarryOverflowMigration.Id, PlayerDeathCarryOverflowMigration.Sql),
        new(ItemOperationsHardeningMigration.Id, ItemOperationsHardeningMigration.Sql)
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            await EnsureMigrationTableAsync(connection, cancellationToken);

            foreach (var migration in Migrations)
            {
                await ApplyMigrationAsync(connection, migration, cancellationToken);
            }
        }

        await itemCatalogSeeder.SeedAsync(cancellationToken);
        await characterItemStateBootstrapper.BackfillActiveCharactersAsync(cancellationToken);
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

    private const string LegacyInitialAuthSchemaMigrationSql = """
        create table accounts (
            id uuid primary key,
            email text not null,
            normalized_email text not null unique,
            username text not null,
            normalized_username text not null unique,
            password_hash text not null,
            created_at timestamptz not null default now()
        );

        create table account_sessions (
            id uuid primary key,
            account_id uuid not null references accounts(id) on delete cascade,
            token_hash text not null unique,
            created_at timestamptz not null default now(),
            expires_at timestamptz not null,
            revoked_at timestamptz null
        );

        create index ix_account_sessions_account_id
            on account_sessions(account_id);

        create index ix_account_sessions_token_hash
            on account_sessions(token_hash)
            where revoked_at is null;

        create table characters (
            id uuid primary key,
            account_id uuid not null references accounts(id) on delete cascade,
            name text not null,
            normalized_name text not null unique,
            currency bigint not null default 0,
            created_at timestamptz not null default now(),
            deleted_at timestamptz null
        );

        create index ix_characters_account_id
            on characters(account_id)
            where deleted_at is null;

        create table worlds (
            id text primary key,
            display_name text not null,
            host text not null,
            udp_port integer not null,
            rule_set text not null,
            is_online boolean not null default true,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now()
        );

        insert into worlds (id, display_name, host, udp_port, rule_set, is_online)
        values ('local-world-1', 'Local World 1', '127.0.0.1', 27015, 'mvp-open-risk', true);

        create table world_join_tickets (
            id uuid primary key,
            ticket_hash text not null unique,
            account_id uuid not null references accounts(id) on delete cascade,
            character_id uuid not null references characters(id) on delete cascade,
            world_id text not null references worlds(id),
            created_at timestamptz not null default now(),
            expires_at timestamptz not null,
            consumed_at timestamptz null
        );

        create index ix_world_join_tickets_character_id
            on world_join_tickets(character_id);

        create index ix_world_join_tickets_ticket_hash
            on world_join_tickets(ticket_hash)
            where consumed_at is null;
        """;

    private const string LegacySimulationSessionLeaseMigrationSql = """
        create table character_world_sessions (
            id uuid primary key,
            session_token_hash text not null unique,
            account_id uuid not null references accounts(id) on delete cascade,
            character_id uuid not null references characters(id) on delete cascade,
            world_id text not null references worlds(id),
            created_at timestamptz not null default now(),
            last_heartbeat_at timestamptz not null default now(),
            expires_at timestamptz not null,
            released_at timestamptz null,
            constraint ck_character_world_sessions_expiry
                check (expires_at > created_at),
            constraint ck_character_world_sessions_heartbeat
                check (last_heartbeat_at >= created_at),
            constraint ck_character_world_sessions_release
                check (released_at is null or released_at >= created_at)
        );

        create unique index ux_character_world_sessions_active_character
            on character_world_sessions(character_id)
            where released_at is null;

        create index ix_character_world_sessions_active_world
            on character_world_sessions(world_id)
            where released_at is null;

        create index ix_character_world_sessions_active_expiry
            on character_world_sessions(expires_at)
            where released_at is null;

        alter table world_join_tickets
            add column world_session_id uuid null references character_world_sessions(id);

        with ranked_tickets as (
            select id,
                   row_number() over (
                       partition by character_id
                       order by created_at desc, id desc) as ticket_rank
            from world_join_tickets
            where consumed_at is null
        )
        update world_join_tickets
        set consumed_at = now()
        where id in (
            select id
            from ranked_tickets
            where ticket_rank > 1
        );

        create unique index ux_world_join_tickets_active_character
            on world_join_tickets(character_id)
            where consumed_at is null;
        """;

    private const string LegacyTicketOwnershipMigrationSql = """
        alter table world_join_tickets
            add column account_session_id uuid null references account_sessions(id) on delete set null;

        create index ix_world_join_tickets_account_session_id
            on world_join_tickets(account_session_id)
            where consumed_at is null;
        """;

    private const string LegacySessionOwnershipMigrationSql = """
        alter table character_world_sessions
            add column account_session_id uuid null references account_sessions(id) on delete set null;

        create index ix_character_world_sessions_account_session_id
            on character_world_sessions(account_session_id)
            where released_at is null;
        """;

    private const string LegacyRegistryHeartbeatMigrationSql = """
        alter table worlds
            add column last_heartbeat_at timestamptz null;

        update worlds
        set is_online = false,
            last_heartbeat_at = null,
            updated_at = now();

        create index ix_worlds_last_heartbeat_at
            on worlds(last_heartbeat_at);
        """;

    private const string LegacySingleActiveAccountSessionMigrationSql = """
        alter table account_sessions
            add column revocation_reason text null;

        update account_sessions
        set revocation_reason = 'legacy_revocation'
        where revoked_at is not null;

        with ranked_sessions as (
            select id,
                   row_number() over (
                       partition by account_id
                       order by created_at desc, id desc) as session_rank
            from account_sessions
            where revoked_at is null
        ), superseded_sessions as (
            select id
            from ranked_sessions
            where session_rank > 1
        )
        update account_sessions
        set revoked_at = now(),
            revocation_reason = 'session_replaced'
        where id in (select id from superseded_sessions);

        update world_join_tickets
        set consumed_at = now()
        where consumed_at is null
          and account_session_id in (
              select id
              from account_sessions
              where revocation_reason = 'session_replaced');

        update character_world_sessions
        set released_at = now()
        where released_at is null
          and account_session_id in (
              select id
              from account_sessions
              where revocation_reason = 'session_replaced');

        alter table account_sessions
            add constraint ck_account_sessions_revocation_reason
            check (
                (revoked_at is null and revocation_reason is null)
                or
                (revoked_at is not null and revocation_reason in (
                    'logout',
                    'manual_revoke',
                    'session_replaced',
                    'legacy_revocation'))
            );

        create unique index ux_account_sessions_active_account
            on account_sessions(account_id)
            where revoked_at is null;
        """;

    private const string LegacyRuntimeRegistrationMigrationSql = """
        alter table worlds
            add column instance_id text null,
            add column protocol_version integer null,
            add column simulation_revision text null,
            add column collision_revision text null;

        update worlds
        set is_online = false,
            updated_at = now()
        where instance_id is null;

        alter table worlds
            add constraint ck_worlds_udp_port
                check (udp_port between 1 and 65535),
            add constraint ck_worlds_protocol_version
                check (protocol_version is null or protocol_version between 1 and 65535),
            add constraint ck_worlds_runtime_metadata
                check (
                    (
                        (instance_id is null
                         and protocol_version is null
                         and simulation_revision is null
                         and collision_revision is null)
                        or
                        (instance_id is not null
                         and protocol_version is not null
                         and simulation_revision is not null
                         and collision_revision is not null)
                    )
                    and (not is_online or instance_id is not null));
        """;

    private const string SimulationTopologyMigrationSql = """
        create table world_definitions (
            id text primary key,
            display_name text not null,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now()
        );

        create table fleets (
            id text primary key,
            display_name text not null,
            region_code text not null,
            is_enabled boolean not null default true,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now()
        );

        insert into world_definitions (id, display_name)
        select id, display_name
        from worlds;

        insert into fleets (id, display_name, region_code)
        values ('local-fleet', 'Local Development', 'LOCAL');

        alter table worlds
            add column world_definition_id text null references world_definitions(id),
            add column fleet_id text null references fleets(id),
            add column is_enabled boolean not null default true;

        update worlds
        set world_definition_id = id,
            fleet_id = 'local-fleet';

        alter table worlds
            alter column world_definition_id set not null,
            alter column fleet_id set not null;

        alter table worlds rename to shards;
        alter table shards rename column world_definition_id to world_id;
        alter table shards rename constraint worlds_pkey to shards_pkey;

        alter table world_join_tickets rename to simulation_join_tickets;
        alter table simulation_join_tickets rename column world_id to shard_id;
        alter table simulation_join_tickets rename column world_session_id to simulation_session_id;
        alter table simulation_join_tickets
            rename constraint world_join_tickets_pkey to simulation_join_tickets_pkey;
        alter table simulation_join_tickets
            rename constraint world_join_tickets_ticket_hash_key to simulation_join_tickets_ticket_hash_key;
        alter table simulation_join_tickets
            rename constraint world_join_tickets_world_id_fkey to simulation_join_tickets_shard_id_fkey;
        alter table simulation_join_tickets
            rename constraint world_join_tickets_world_session_id_fkey to simulation_join_tickets_simulation_session_id_fkey;

        alter table character_world_sessions rename to character_simulation_sessions;
        alter table character_simulation_sessions rename column world_id to shard_id;
        alter table character_simulation_sessions
            rename constraint character_world_sessions_pkey to character_simulation_sessions_pkey;
        alter table character_simulation_sessions
            rename constraint character_world_sessions_session_token_hash_key to character_simulation_sessions_session_token_hash_key;
        alter table character_simulation_sessions
            rename constraint character_world_sessions_world_id_fkey to character_simulation_sessions_shard_id_fkey;
        alter table character_simulation_sessions
            rename constraint ck_character_world_sessions_expiry to ck_character_simulation_sessions_expiry;
        alter table character_simulation_sessions
            rename constraint ck_character_world_sessions_heartbeat to ck_character_simulation_sessions_heartbeat;
        alter table character_simulation_sessions
            rename constraint ck_character_world_sessions_release to ck_character_simulation_sessions_release;

        alter index ix_world_join_tickets_character_id rename to ix_simulation_join_tickets_character_id;
        alter index ix_world_join_tickets_ticket_hash rename to ix_simulation_join_tickets_ticket_hash;
        alter index ux_world_join_tickets_active_character rename to ux_simulation_join_tickets_active_character;
        alter index ix_world_join_tickets_account_session_id rename to ix_simulation_join_tickets_account_session_id;
        alter index ux_character_world_sessions_active_character rename to ux_character_simulation_sessions_active_character;
        alter index ix_character_world_sessions_active_world rename to ix_character_simulation_sessions_active_shard;
        alter index ix_character_world_sessions_active_expiry rename to ix_character_simulation_sessions_active_expiry;
        alter index ix_character_world_sessions_account_session_id rename to ix_character_simulation_sessions_account_session_id;
        alter index ix_worlds_last_heartbeat_at rename to ix_legacy_shards_last_heartbeat_at;

        insert into shards (
            id,
            display_name,
            host,
            udp_port,
            rule_set,
            is_online,
            created_at,
            updated_at,
            last_heartbeat_at,
            instance_id,
            protocol_version,
            simulation_revision,
            collision_revision,
            world_id,
            fleet_id,
            is_enabled)
        select
            'local-shard-1',
            'Local Shard 1',
            host,
            udp_port,
            rule_set,
            is_online,
            created_at,
            updated_at,
            last_heartbeat_at,
            instance_id,
            protocol_version,
            simulation_revision,
            collision_revision,
            world_id,
            fleet_id,
            is_enabled
        from shards
        where id = 'local-world-1'
        on conflict (id) do nothing;

        update simulation_join_tickets
        set shard_id = 'local-shard-1'
        where shard_id = 'local-world-1';

        update character_simulation_sessions
        set shard_id = 'local-shard-1'
        where shard_id = 'local-world-1';

        delete from shards
        where id = 'local-world-1'
          and exists (select 1 from shards where id = 'local-shard-1');

        create table simulation_nodes (
            id text primary key,
            fleet_id text not null references fleets(id),
            display_name text not null,
            is_enabled boolean not null default true,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now()
        );

        insert into simulation_nodes (id, fleet_id, display_name)
        values ('local-node-1', 'local-fleet', 'Local Node 1');

        create table simulation_workers (
            id text primary key,
            node_id text not null references simulation_nodes(id),
            runtime_id text null,
            started_at timestamptz null,
            host text null,
            udp_port integer null,
            max_connections integer not null default 1,
            active_connections integer not null default 0,
            protocol_version integer null,
            simulation_revision text null,
            collision_revision text null,
            is_online boolean not null default false,
            last_heartbeat_at timestamptz null,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now(),
            constraint ck_simulation_workers_udp_port
                check (udp_port is null or udp_port between 1 and 65535),
            constraint ck_simulation_workers_capacity
                check (max_connections > 0 and active_connections between 0 and max_connections),
            constraint ck_simulation_workers_protocol_version
                check (protocol_version is null or protocol_version between 1 and 65535),
            constraint ck_simulation_workers_runtime_metadata
                check (
                    (
                        runtime_id is null
                        and started_at is null
                        and host is null
                        and udp_port is null
                        and protocol_version is null
                        and simulation_revision is null
                        and collision_revision is null
                        and last_heartbeat_at is null
                        and not is_online
                    )
                    or
                    (
                        runtime_id is not null
                        and started_at is not null
                        and host is not null
                        and udp_port is not null
                        and protocol_version is not null
                        and simulation_revision is not null
                        and collision_revision is not null
                        and last_heartbeat_at is not null
                    )
                )
        );

        create table simulation_assignments (
            id text primary key,
            worker_id text not null references simulation_workers(id),
            shard_id text not null references shards(id),
            assigned_at timestamptz not null default now(),
            released_at timestamptz null,
            constraint ck_simulation_assignments_release
                check (released_at is null or released_at >= assigned_at)
        );

        create unique index ux_simulation_assignments_active_worker
            on simulation_assignments(worker_id)
            where released_at is null;

        create unique index ux_simulation_assignments_active_shard
            on simulation_assignments(shard_id)
            where released_at is null;

        create index ix_simulation_workers_online_heartbeat
            on simulation_workers(last_heartbeat_at)
            where is_online;

        insert into simulation_workers (
            id,
            node_id,
            runtime_id,
            started_at,
            host,
            udp_port,
            max_connections,
            active_connections,
            protocol_version,
            simulation_revision,
            collision_revision,
            is_online,
            last_heartbeat_at,
            updated_at)
        select
            case
                when id = 'local-shard-1' then 'local-simulation-worker-1'
                else 'migrated-worker-' || substr(md5(id), 1, 16)
            end,
            'local-node-1',
            instance_id,
            case when instance_id is null then null else coalesce(last_heartbeat_at, updated_at) end,
            case when instance_id is null then null else host end,
            case when instance_id is null then null else udp_port end,
            100,
            0,
            protocol_version,
            simulation_revision,
            collision_revision,
            is_online and instance_id is not null,
            last_heartbeat_at,
            updated_at
        from shards;

        insert into simulation_assignments (id, worker_id, shard_id)
        select
            'assignment-' || substr(md5(id), 1, 24),
            case
                when id = 'local-shard-1' then 'local-simulation-worker-1'
                else 'migrated-worker-' || substr(md5(id), 1, 16)
            end,
            id
        from shards;

        alter table simulation_join_tickets
            add column simulation_worker_id text null references simulation_workers(id),
            add column worker_runtime_id text null;

        alter table character_simulation_sessions
            add column simulation_worker_id text null references simulation_workers(id),
            add column worker_runtime_id text null;

        update simulation_join_tickets ticket
        set simulation_worker_id = assignment.worker_id,
            worker_runtime_id = worker.runtime_id
        from simulation_assignments assignment
        join simulation_workers worker on worker.id = assignment.worker_id
        where assignment.shard_id = ticket.shard_id
          and assignment.released_at is null;

        update character_simulation_sessions session
        set simulation_worker_id = assignment.worker_id,
            worker_runtime_id = worker.runtime_id
        from simulation_assignments assignment
        join simulation_workers worker on worker.id = assignment.worker_id
        where assignment.shard_id = session.shard_id
          and assignment.released_at is null;

        update simulation_join_tickets
        set consumed_at = coalesce(consumed_at, now())
        where simulation_worker_id is null or worker_runtime_id is null;

        update character_simulation_sessions
        set released_at = coalesce(released_at, now())
        where simulation_worker_id is null or worker_runtime_id is null;

        alter table simulation_join_tickets
            add constraint ck_simulation_join_tickets_worker_binding
            check (
                consumed_at is not null
                or (simulation_worker_id is not null and worker_runtime_id is not null)
            );

        alter table character_simulation_sessions
            add constraint ck_character_simulation_sessions_worker_binding
            check (
                released_at is not null
                or (simulation_worker_id is not null and worker_runtime_id is not null)
            );

        create index ix_simulation_join_tickets_worker_runtime
            on simulation_join_tickets(simulation_worker_id, worker_runtime_id)
            where consumed_at is null;

        create index ix_character_simulation_sessions_worker_runtime
            on character_simulation_sessions(simulation_worker_id, worker_runtime_id)
            where released_at is null;

        alter table shards
            drop constraint if exists ck_worlds_udp_port,
            drop constraint if exists ck_worlds_protocol_version,
            drop constraint if exists ck_worlds_runtime_metadata;

        alter table shards
            drop column host,
            drop column udp_port,
            drop column is_online,
            drop column last_heartbeat_at,
            drop column instance_id,
            drop column protocol_version,
            drop column simulation_revision,
            drop column collision_revision;

        drop index if exists ix_legacy_shards_last_heartbeat_at;

        create index ix_shards_fleet_id on shards(fleet_id);
        create index ix_shards_world_id on shards(world_id);
        create index ix_simulation_nodes_fleet_id on simulation_nodes(fleet_id);
        create index ix_simulation_assignments_shard_id on simulation_assignments(shard_id);
        """;

    private const string SingleActiveAccountSimulationSessionMigrationSql = """
        with ranked_sessions as (
            select id,
                   row_number() over (
                       partition by account_id
                       order by created_at desc, id desc) as session_rank
            from character_simulation_sessions
            where released_at is null
        )
        update character_simulation_sessions
        set released_at = now()
        where id in (
            select id
            from ranked_sessions
            where session_rank > 1
        );

        create unique index ux_character_simulation_sessions_active_account
            on character_simulation_sessions(account_id)
            where released_at is null;
        """;

    private sealed record DatabaseMigration(string Id, string Sql);
}
