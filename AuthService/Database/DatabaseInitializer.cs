using Dapper;
using Npgsql;

namespace AuthService.Database;

public sealed class DatabaseInitializer(NpgsqlDataSource dataSource, ILogger<DatabaseInitializer> logger)
{
    private const string MigrationId = "202607071600_auth_character_world_join";

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

        var alreadyApplied = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "select exists (select 1 from schema_migrations where id = @MigrationId);",
                new { MigrationId },
                cancellationToken: cancellationToken));

        if (alreadyApplied)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                MigrationSql,
                transaction: transaction,
                cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(
                "insert into schema_migrations (id) values (@MigrationId);",
                new { MigrationId },
                transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Applied database migration {MigrationId}.", MigrationId);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private const string MigrationSql = """
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
}

