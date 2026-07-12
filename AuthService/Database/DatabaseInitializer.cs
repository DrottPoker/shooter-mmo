using Dapper;
using Npgsql;

namespace AuthService.Database;

public sealed class DatabaseInitializer(NpgsqlDataSource dataSource, ILogger<DatabaseInitializer> logger)
{
    private const long MigrationLockId = 7_104_202_607_071_600;

    private static readonly IReadOnlyCollection<DatabaseMigration> Migrations =
    [
        new("202607071600_auth_character_world_join", InitialMigrationSql),
        new("202607101200_character_world_sessions", WorldSessionMigrationSql),
        new("202607121200_session_ticket_ownership", SessionTicketOwnershipMigrationSql),
        new("202607121500_world_session_ownership", WorldSessionOwnershipMigrationSql)
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            create table if not exists schema_migrations (
                id text primary key,
                applied_at timestamptz not null default now()
            );
            """,
            cancellationToken: cancellationToken));

        foreach (var migration in Migrations)
        {
            await ApplyMigrationAsync(connection, migration, cancellationToken);
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

    private const string InitialMigrationSql = """
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

    private const string WorldSessionMigrationSql = """
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

    private const string SessionTicketOwnershipMigrationSql = """
        alter table world_join_tickets
            add column account_session_id uuid null references account_sessions(id) on delete set null;

        create index ix_world_join_tickets_account_session_id
            on world_join_tickets(account_session_id)
            where consumed_at is null;
        """;

    private const string WorldSessionOwnershipMigrationSql = """
        alter table character_world_sessions
            add column account_session_id uuid null references account_sessions(id) on delete set null;

        create index ix_character_world_sessions_account_session_id
            on character_world_sessions(account_session_id)
            where released_at is null;
        """;

    private sealed record DatabaseMigration(string Id, string Sql);
}
