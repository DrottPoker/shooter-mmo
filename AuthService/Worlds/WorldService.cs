using AuthService.Auth;
using AuthService.Http;
using Dapper;
using Npgsql;

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

        if (string.IsNullOrWhiteSpace(worldId))
        {
            return ServiceResult<JoinWorldResponse>.BadRequest("invalid_world_id", "World id is required.");
        }

        var normalizedWorldId = worldId.Trim();
        var ticketLifetimeSeconds = configuration.GetValue("WorldJoin:TicketLifetimeSeconds", 30);
        var ticket = TokenGenerator.CreateToken();
        var ticketHash = TokenGenerator.HashToken(ticket);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var world = await GetWorldAsync(connection, transaction, normalizedWorldId, cancellationToken);
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

        var ownsCharacter = await LockOwnedCharacterAsync(
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

        await ReleaseExpiredWorldSessionsAsync(
            connection,
            transaction,
            request.CharacterId,
            cancellationToken);

        var activeSession = await GetActiveWorldSessionAsync(
            connection,
            transaction,
            request.CharacterId,
            cancellationToken);

        if (activeSession is not null
            && !string.Equals(activeSession.WorldId, world.Id, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<JoinWorldResponse>.Conflict(
                "character_already_active",
                $"Character is already active on world {activeSession.WorldId}.");
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
                (id, ticket_hash, account_id, account_session_id, character_id, world_id, expires_at, world_session_id)
            values
                (@Id, @TicketHash, @AccountId, @AccountSessionId, @CharacterId, @WorldId,
                 now() + make_interval(secs => @TicketLifetimeSeconds), @WorldSessionId)
            returning expires_at;
            """;

        var expiresAt = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            insertTicketSql,
            new
            {
                Id = Guid.NewGuid(),
                TicketHash = ticketHash,
                AccountId = account.AccountId,
                AccountSessionId = account.SessionId,
                request.CharacterId,
                WorldId = world.Id,
                TicketLifetimeSeconds = ticketLifetimeSeconds,
                WorldSessionId = activeSession?.Id
            },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return ServiceResult<JoinWorldResponse>.Ok(new JoinWorldResponse(
            world,
            request.CharacterId,
            ticket,
            expiresAt,
            activeSession is not null));
    }

    public async Task<ServiceResult<ConsumedJoinTicketResponse>> ConsumeJoinTicketAsync(
        ConsumeJoinTicketRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Ticket))
        {
            return ServiceResult<ConsumedJoinTicketResponse>.BadRequest("invalid_ticket", "Join ticket is required.");
        }

        if (string.IsNullOrWhiteSpace(request.WorldId))
        {
            return ServiceResult<ConsumedJoinTicketResponse>.BadRequest("invalid_world_id", "World id is required.");
        }

        var ticketHash = TokenGenerator.HashToken(request.Ticket.Trim());
        var expectedWorldId = request.WorldId.Trim();
        var sessionToken = TokenGenerator.CreateToken();
        var sessionTokenHash = TokenGenerator.HashToken(sessionToken);
        var leaseLifetimeSeconds = configuration.GetValue("WorldSession:LeaseLifetimeSeconds", 30);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var candidateCharacterId = await FindTicketCharacterIdAsync(
            connection,
            transaction,
            ticketHash,
            cancellationToken);

        if (candidateCharacterId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvalidTicket();
        }

        await LockCharacterAsync(
            connection,
            transaction,
            candidateCharacterId.Value,
            cancellationToken);

        var ticket = await GetValidJoinTicketAsync(
            connection,
            transaction,
            ticketHash,
            cancellationToken);

        if (ticket is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvalidTicket();
        }

        if (!string.Equals(ticket.WorldId, expectedWorldId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<ConsumedJoinTicketResponse>.Conflict(
                "wrong_world",
                $"Join ticket is for world {ticket.WorldId}, not {expectedWorldId}.");
        }

        await ReleaseExpiredWorldSessionsAsync(
            connection,
            transaction,
            ticket.CharacterId,
            cancellationToken);

        var activeSession = await GetActiveWorldSessionAsync(
            connection,
            transaction,
            ticket.CharacterId,
            cancellationToken);

        if (activeSession is not null
            && !string.Equals(activeSession.WorldId, ticket.WorldId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<ConsumedJoinTicketResponse>.Conflict(
                "character_already_active",
                $"Character is already active on world {activeSession.WorldId}.");
        }

        if (activeSession is not null && ticket.TargetWorldSessionId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<ConsumedJoinTicketResponse>.Conflict(
                "character_already_active",
                "Character became active after this join ticket was issued.");
        }

        if (activeSession is not null && ticket.TargetWorldSessionId != activeSession.Id)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<ConsumedJoinTicketResponse>.Conflict(
                "world_session_changed",
                "The reconnect target changed after this join ticket was issued.");
        }

        var isReconnect = activeSession is not null
            && ticket.TargetWorldSessionId == activeSession.Id;
        var worldSession = activeSession is null
            ? await CreateWorldSessionAsync(
                connection,
                transaction,
                ticket,
                sessionTokenHash,
                leaseLifetimeSeconds,
                cancellationToken)
            : await RefreshWorldSessionAsync(
                connection,
                transaction,
                activeSession.Id,
                ticket.AccountSessionId,
                sessionTokenHash,
                leaseLifetimeSeconds,
                cancellationToken);

        const string consumeTicketSql = """
            update world_join_tickets
            set consumed_at = now(),
                world_session_id = @WorldSessionId
            where id = @TicketId and consumed_at is null;
            """;

        var consumedCount = await connection.ExecuteAsync(new CommandDefinition(
            consumeTicketSql,
            new { WorldSessionId = worldSession.Id, ticket.TicketId },
            transaction,
            cancellationToken: cancellationToken));

        if (consumedCount != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvalidTicket();
        }

        await transaction.CommitAsync(cancellationToken);

        return ServiceResult<ConsumedJoinTicketResponse>.Ok(new ConsumedJoinTicketResponse(
            ticket.AccountId,
            ticket.CharacterId,
            ticket.CharacterName,
            ticket.WorldId,
            worldSession.Id,
            sessionToken,
            worldSession.ExpiresAt,
            isReconnect));
    }

    private static ServiceResult<ConsumedJoinTicketResponse> InvalidTicket()
    {
        return ServiceResult<ConsumedJoinTicketResponse>.Unauthorized(
            "invalid_ticket",
            "Join ticket is invalid or expired.");
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

    private static async Task<bool> LockOwnedCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id
            from characters
            where id = @CharacterId
              and account_id = @AccountId
              and deleted_at is null
            for update;
            """;

        var lockedCharacterId = await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                sql,
                new { AccountId = accountId, CharacterId = characterId },
                transaction,
                cancellationToken: cancellationToken));

        return lockedCharacterId is not null;
    }

    private static async Task LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id
            from characters
            where id = @CharacterId
            for update;
            """;

        await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql,
            new { CharacterId = characterId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<Guid?> FindTicketCharacterIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string ticketHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select character_id
            from world_join_tickets
            where ticket_hash = @TicketHash;
            """;

        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            sql,
            new { TicketHash = ticketHash },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<JoinTicketRow?> GetValidJoinTicketAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string ticketHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select t.id as "TicketId",
                   t.account_id as "AccountId",
                   t.account_session_id as "AccountSessionId",
                   t.character_id as "CharacterId",
                   c.name as "CharacterName",
                   t.world_id as "WorldId",
                   t.world_session_id as "TargetWorldSessionId"
            from world_join_tickets t
            join characters c on c.id = t.character_id and c.deleted_at is null
            where t.ticket_hash = @TicketHash
              and t.consumed_at is null
              and t.expires_at > now()
            for update of t;
            """;

        return await connection.QuerySingleOrDefaultAsync<JoinTicketRow>(new CommandDefinition(
            sql,
            new { TicketHash = ticketHash },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task ReleaseExpiredWorldSessionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update character_world_sessions
            set released_at = now()
            where character_id = @CharacterId
              and released_at is null
              and expires_at <= now();
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { CharacterId = characterId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<ActiveWorldSessionRow?> GetActiveWorldSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id as "Id",
                   world_id as "WorldId",
                   expires_at as "ExpiresAt"
            from character_world_sessions
            where character_id = @CharacterId
              and released_at is null
            for update;
            """;

        return await connection.QuerySingleOrDefaultAsync<ActiveWorldSessionRow>(new CommandDefinition(
            sql,
            new { CharacterId = characterId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<ActiveWorldSessionRow> CreateWorldSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        JoinTicketRow ticket,
        string sessionTokenHash,
        int leaseLifetimeSeconds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into character_world_sessions
                (id, session_token_hash, account_id, account_session_id, character_id, world_id, expires_at)
            values
                (@Id, @SessionTokenHash, @AccountId, @AccountSessionId, @CharacterId, @WorldId,
                 now() + make_interval(secs => @LeaseLifetimeSeconds))
            returning id as "Id", world_id as "WorldId", expires_at as "ExpiresAt";
            """;

        return await connection.QuerySingleAsync<ActiveWorldSessionRow>(new CommandDefinition(
            sql,
            new
            {
                Id = Guid.NewGuid(),
                SessionTokenHash = sessionTokenHash,
                ticket.AccountId,
                ticket.AccountSessionId,
                ticket.CharacterId,
                ticket.WorldId,
                LeaseLifetimeSeconds = leaseLifetimeSeconds
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<ActiveWorldSessionRow> RefreshWorldSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid worldSessionId,
        Guid? accountSessionId,
        string sessionTokenHash,
        int leaseLifetimeSeconds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update character_world_sessions
            set session_token_hash = @SessionTokenHash,
                account_session_id = @AccountSessionId,
                last_heartbeat_at = now(),
                expires_at = now() + make_interval(secs => @LeaseLifetimeSeconds)
            where id = @WorldSessionId and released_at is null
            returning id as "Id", world_id as "WorldId", expires_at as "ExpiresAt";
            """;

        return await connection.QuerySingleAsync<ActiveWorldSessionRow>(new CommandDefinition(
            sql,
            new
            {
                WorldSessionId = worldSessionId,
                AccountSessionId = accountSessionId,
                SessionTokenHash = sessionTokenHash,
                LeaseLifetimeSeconds = leaseLifetimeSeconds
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed record JoinTicketRow(
        Guid TicketId,
        Guid AccountId,
        Guid? AccountSessionId,
        Guid CharacterId,
        string CharacterName,
        string WorldId,
        Guid? TargetWorldSessionId);

    private sealed record ActiveWorldSessionRow(Guid Id, string WorldId, DateTime ExpiresAt);
}
