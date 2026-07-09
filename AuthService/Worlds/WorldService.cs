using Dapper;
using Npgsql;
using AuthService.Auth;
using AuthService.Http;

namespace AuthService.Worlds;

public sealed class WorldService(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    public async Task<ServiceResult<IReadOnlyCollection<WorldResponse>>> ListWorldsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id as "Id",
                   display_name as "DisplayName",
                   host as "Host",
                   udp_port as "UdpPort",
                   rule_set as "RuleSet",
                   is_online as "IsOnline"
            from worlds
            order by id asc;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var worlds = await connection.QueryAsync<WorldResponse>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));

        return ServiceResult<IReadOnlyCollection<WorldResponse>>.Ok(worlds.ToArray());
    }

    public async Task<ServiceResult<JoinWorldResponse>> CreateJoinTicketAsync(
        AuthenticatedAccount account,
        string worldId,
        JoinWorldRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CharacterId == Guid.Empty)
        {
            return ServiceResult<JoinWorldResponse>.BadRequest("invalid_character_id", "Character id is required.");
        }

        var ticketLifetimeSeconds = configuration.GetValue("WorldJoin:TicketLifetimeSeconds", 30);
        var ticket = TokenGenerator.CreateToken();
        var ticketHash = TokenGenerator.HashToken(ticket);
        var expiresAt = DateTime.UtcNow.AddSeconds(ticketLifetimeSeconds);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var world = await GetWorldAsync(connection, transaction, worldId, cancellationToken);
        if (world is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<JoinWorldResponse>.NotFound("world_not_found", "World was not found.");
        }

        if (!world.IsOnline)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<JoinWorldResponse>.Conflict("world_offline", "World is offline.");
        }

        var ownsCharacter = await CharacterBelongsToAccountAsync(
            connection,
            transaction,
            account.AccountId,
            request.CharacterId,
            cancellationToken);

        if (!ownsCharacter)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<JoinWorldResponse>.NotFound("character_not_found", "Character was not found.");
        }

        const string consumeOldTicketsSql = """
            update world_join_tickets
            set consumed_at = now()
            where character_id = @CharacterId and consumed_at is null;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            consumeOldTicketsSql,
            new { request.CharacterId },
            transaction,
            cancellationToken: cancellationToken));

        const string insertTicketSql = """
            insert into world_join_tickets
                (id, ticket_hash, account_id, character_id, world_id, expires_at)
            values
                (@Id, @TicketHash, @AccountId, @CharacterId, @WorldId, @ExpiresAt);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            insertTicketSql,
            new
            {
                Id = Guid.NewGuid(),
                TicketHash = ticketHash,
                AccountId = account.AccountId,
                request.CharacterId,
                WorldId = world.Id,
                ExpiresAt = expiresAt
            },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return ServiceResult<JoinWorldResponse>.Ok(new JoinWorldResponse(
            world,
            request.CharacterId,
            ticket,
            expiresAt));
    }

    public async Task<ServiceResult<ConsumedJoinTicketResponse>> ConsumeJoinTicketAsync(
        ConsumeJoinTicketRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Ticket))
        {
            return ServiceResult<ConsumedJoinTicketResponse>.BadRequest("invalid_ticket", "Join ticket is required.");
        }

        var ticketHash = TokenGenerator.HashToken(request.Ticket.Trim());

        const string sql = """
            with consumed_ticket as (
                update world_join_tickets
                set consumed_at = now()
                where ticket_hash = @TicketHash
                  and consumed_at is null
                  and expires_at > now()
                returning account_id, character_id, world_id, expires_at
            )
            select ct.account_id as "AccountId",
                   ct.character_id as "CharacterId",
                   c.name as "CharacterName",
                   ct.world_id as "WorldId",
                   ct.expires_at as "ExpiresAt"
            from consumed_ticket ct
            join characters c on c.id = ct.character_id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var consumedTicket = await connection.QuerySingleOrDefaultAsync<ConsumedJoinTicketResponse>(
            new CommandDefinition(sql, new { TicketHash = ticketHash }, cancellationToken: cancellationToken));

        return consumedTicket is null
            ? ServiceResult<ConsumedJoinTicketResponse>.Unauthorized("invalid_ticket", "Join ticket is invalid or expired.")
            : ServiceResult<ConsumedJoinTicketResponse>.Ok(consumedTicket);
    }

    private static async Task<WorldResponse?> GetWorldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id as "Id",
                   display_name as "DisplayName",
                   host as "Host",
                   udp_port as "UdpPort",
                   rule_set as "RuleSet",
                   is_online as "IsOnline"
            from worlds
            where id = @WorldId;
            """;

        return await connection.QuerySingleOrDefaultAsync<WorldResponse>(
            new CommandDefinition(sql, new { WorldId = worldId }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task<bool> CharacterBelongsToAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select exists (
                select 1
                from characters
                where id = @CharacterId
                  and account_id = @AccountId
                  and deleted_at is null
            );
            """;

        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                sql,
                new { AccountId = accountId, CharacterId = characterId },
                transaction,
                cancellationToken: cancellationToken));
    }
}
