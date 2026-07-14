using AuthService.Auth;
using AuthService.Config;
using AuthService.Http;
using Dapper;
using Npgsql;

namespace AuthService.Simulation;

public sealed class ShardService(
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    AuthServiceConfig config)
{
    public async Task<ServiceResult<IReadOnlyCollection<ShardResponse>>> ListShardsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            select shard.id as "Id",
                   shard.display_name as "DisplayName",
                   shard.world_id as "WorldId",
                   fleet.id as "FleetId",
                   fleet.display_name as "FleetDisplayName",
                   fleet.region_code as "RegionCode",
                   shard.rule_set as "RuleSet",
                   coalesce(bool_or(
                       worker.is_online
                       and worker.last_heartbeat_at is not null
                       and worker.last_heartbeat_at
                           + make_interval(secs => @HeartbeatTimeoutSeconds) > now()
                       and greatest(
                               worker.active_connections,
                               coalesce(session_usage.active_players, 0))
                           + coalesce(ticket_usage.pending_tickets, 0)
                           < worker.max_connections), false) as "IsOnline",
                   coalesce(sum(greatest(
                       worker.active_connections,
                       coalesce(session_usage.active_players, 0))) filter (
                       where worker.is_online
                          and worker.last_heartbeat_at is not null
                          and worker.last_heartbeat_at
                              + make_interval(secs => @HeartbeatTimeoutSeconds) > now()), 0)::integer
                       as "ActivePlayers",
                   coalesce(sum(worker.max_connections) filter (
                       where worker.is_online
                         and worker.last_heartbeat_at is not null
                         and worker.last_heartbeat_at
                             + make_interval(secs => @HeartbeatTimeoutSeconds) > now()), 0)::integer
                       as "Capacity"
            from shards shard
            join fleets fleet on fleet.id = shard.fleet_id
            left join simulation_assignments assignment
              on assignment.shard_id = shard.id
             and assignment.released_at is null
            left join simulation_workers worker on worker.id = assignment.worker_id
            left join lateral (
                select count(*)::integer as active_players
                from character_simulation_sessions simulation_session
                where simulation_session.simulation_worker_id = worker.id
                  and simulation_session.worker_runtime_id = worker.runtime_id
                  and simulation_session.released_at is null
                  and simulation_session.expires_at > now()
            ) session_usage on true
            left join lateral (
                select count(*)::integer as pending_tickets
                from simulation_join_tickets pending_ticket
                where pending_ticket.simulation_worker_id = worker.id
                  and pending_ticket.worker_runtime_id = worker.runtime_id
                  and pending_ticket.consumed_at is null
                  and pending_ticket.expires_at > now()
            ) ticket_usage on true
            where shard.is_enabled
              and fleet.is_enabled
            group by shard.id, fleet.id
            order by fleet.id, shard.id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var shards = await connection.QueryAsync<ShardResponse>(new CommandDefinition(
            sql,
            new
            {
                HeartbeatTimeoutSeconds = (int)config.SimulationWorkerHeartbeatTimeout.TotalSeconds
            },
            cancellationToken: cancellationToken));

        return ServiceResult<IReadOnlyCollection<ShardResponse>>.Ok(shards.ToArray());
    }

    public async Task<ServiceResult<JoinShardResponse>> CreateJoinTicketAsync(
        AuthenticatedAccount account,
        string shardId,
        JoinShardRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CharacterId == Guid.Empty)
        {
            return ServiceResult<JoinShardResponse>.BadRequest(
                "invalid_character_id",
                "Character id is required.");
        }

        if (!IsValidIdentifier(shardId))
        {
            return ServiceResult<JoinShardResponse>.BadRequest(
                "invalid_shard_id",
                "Shard id is invalid.");
        }

        var normalizedShardId = shardId.Trim();
        var ticketLifetimeSeconds = configuration.GetValue(
            "Simulation:JoinTicketLifetimeSeconds",
            30);
        var ticket = TokenGenerator.CreateToken();
        var ticketHash = TokenGenerator.HashToken(ticket);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await LockAccountAsync(
            connection,
            transaction,
            account.AccountId,
            cancellationToken);

        var ownsCharacter = await LockOwnedCharacterAsync(
            connection,
            transaction,
            account.AccountId,
            request.CharacterId,
            cancellationToken);
        if (!ownsCharacter)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<JoinShardResponse>.NotFound(
                "character_not_found",
                "Character was not found.");
        }

        var shardExists = await LockEnabledShardAsync(
            connection,
            transaction,
            normalizedShardId,
            cancellationToken);
        if (!shardExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<JoinShardResponse>.NotFound(
                "shard_not_found",
                "Shard was not found.");
        }

        await ReleaseExpiredSimulationSessionsForAccountAsync(
            connection,
            transaction,
            account.AccountId,
            cancellationToken);

        var activeSession = await GetActiveSimulationSessionForAccountAsync(
            connection,
            transaction,
            account.AccountId,
            cancellationToken);
        if (activeSession is not null && activeSession.CharacterId != request.CharacterId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<JoinShardResponse>.Conflict(
                "account_character_already_active",
                "Another character on this account already has an active simulation session.");
        }

        if (activeSession is not null
            && !string.Equals(activeSession.ShardId, normalizedShardId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<JoinShardResponse>.Conflict(
                "character_already_active",
                $"Character is already active on shard {activeSession.ShardId}.");
        }

        var placement = activeSession is null
            ? await SelectPlacementAsync(
                connection,
                transaction,
                normalizedShardId,
                (int)config.SimulationWorkerHeartbeatTimeout.TotalSeconds,
                cancellationToken)
            : await SelectReconnectPlacementAsync(
                connection,
                transaction,
                activeSession,
                (int)config.SimulationWorkerHeartbeatTimeout.TotalSeconds,
                cancellationToken);

        if (placement is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<JoinShardResponse>.Conflict(
                activeSession is null
                    ? "shard_unavailable"
                    : "simulation_session_worker_unavailable",
                activeSession is null
                    ? "No healthy simulation worker currently has capacity for this shard."
                    : "The simulation worker that owns the active character session is unavailable.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            update simulation_join_tickets
            set consumed_at = now()
            where account_id = @AccountId
              and consumed_at is null;
            """,
            new { AccountId = account.AccountId },
            transaction,
            cancellationToken: cancellationToken));

        const string insertTicketSql = """
            insert into simulation_join_tickets (
                id,
                ticket_hash,
                account_id,
                account_session_id,
                character_id,
                shard_id,
                simulation_worker_id,
                worker_runtime_id,
                expires_at,
                simulation_session_id)
            values (
                @Id,
                @TicketHash,
                @AccountId,
                @AccountSessionId,
                @CharacterId,
                @ShardId,
                @WorkerId,
                @WorkerRuntimeId,
                now() + make_interval(secs => @TicketLifetimeSeconds),
                @SimulationSessionId)
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
                ShardId = placement.ShardId,
                placement.WorkerId,
                WorkerRuntimeId = placement.RuntimeId,
                TicketLifetimeSeconds = ticketLifetimeSeconds,
                SimulationSessionId = activeSession?.Id
            },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return ServiceResult<JoinShardResponse>.Ok(new JoinShardResponse(
            placement.ToShardResponse(),
            placement.ToEndpointResponse(),
            request.CharacterId,
            ticket,
            expiresAt,
            activeSession is not null));
    }

    public async Task<ServiceResult<ConsumedSimulationJoinTicketResponse>> ConsumeJoinTicketAsync(
        ConsumeSimulationJoinTicketRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateConsumeRequest(request);
        if (validationError is not null)
        {
            return validationError;
        }

        var ticketHash = TokenGenerator.HashToken(request.Ticket!.Trim());
        var sessionToken = TokenGenerator.CreateToken();
        var sessionTokenHash = TokenGenerator.HashToken(sessionToken);
        var leaseLifetimeSeconds = configuration.GetValue(
            "Simulation:SessionLeaseLifetimeSeconds",
            30);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var ticketOwner = await FindTicketOwnerAsync(
            connection,
            transaction,
            ticketHash,
            cancellationToken);
        if (ticketOwner is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvalidTicket();
        }

        await LockAccountAsync(
            connection,
            transaction,
            ticketOwner.AccountId,
            cancellationToken);

        await LockCharacterAsync(
            connection,
            transaction,
            ticketOwner.CharacterId,
            cancellationToken);

        var joinTicket = await GetValidJoinTicketAsync(
            connection,
            transaction,
            ticketHash,
            cancellationToken);
        if (joinTicket is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvalidTicket();
        }

        if (!string.Equals(joinTicket.ShardId, request.ShardId!.Trim(), StringComparison.Ordinal)
            || !string.Equals(joinTicket.WorkerId, request.WorkerId!.Trim(), StringComparison.Ordinal)
            || !string.Equals(
                joinTicket.WorkerRuntimeId,
                request.RuntimeId!.Trim(),
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<ConsumedSimulationJoinTicketResponse>.Conflict(
                "wrong_simulation_worker",
                "Join ticket is bound to another shard or simulation worker runtime.");
        }

        await ReleaseExpiredSimulationSessionsForAccountAsync(
            connection,
            transaction,
            joinTicket.AccountId,
            cancellationToken);

        var activeSession = await GetActiveSimulationSessionForAccountAsync(
            connection,
            transaction,
            joinTicket.AccountId,
            cancellationToken);
        if (activeSession is not null && activeSession.CharacterId != joinTicket.CharacterId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<ConsumedSimulationJoinTicketResponse>.Conflict(
                "account_character_already_active",
                "Another character on this account already has an active simulation session.");
        }

        if (activeSession is not null
            && !string.Equals(activeSession.ShardId, joinTicket.ShardId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<ConsumedSimulationJoinTicketResponse>.Conflict(
                "character_already_active",
                $"Character is already active on shard {activeSession.ShardId}.");
        }

        if (activeSession is not null && joinTicket.TargetSimulationSessionId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<ConsumedSimulationJoinTicketResponse>.Conflict(
                "character_already_active",
                "Character became active after this join ticket was issued.");
        }

        if (activeSession is not null
            && joinTicket.TargetSimulationSessionId != activeSession.Id)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<ConsumedSimulationJoinTicketResponse>.Conflict(
                "simulation_session_changed",
                "The reconnect target changed after this join ticket was issued.");
        }

        var isReconnect = activeSession is not null;
        var simulationSession = activeSession is null
            ? await CreateSimulationSessionAsync(
                connection,
                transaction,
                joinTicket,
                sessionTokenHash,
                leaseLifetimeSeconds,
                cancellationToken)
            : await RefreshSimulationSessionAsync(
                connection,
                transaction,
                activeSession.Id,
                joinTicket,
                sessionTokenHash,
                leaseLifetimeSeconds,
                cancellationToken);

        var consumedCount = await connection.ExecuteAsync(new CommandDefinition(
            """
            update simulation_join_tickets
            set consumed_at = now(),
                simulation_session_id = @SimulationSessionId
            where id = @TicketId
              and consumed_at is null;
            """,
            new
            {
                SimulationSessionId = simulationSession.Id,
                joinTicket.TicketId
            },
            transaction,
            cancellationToken: cancellationToken));
        if (consumedCount != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvalidTicket();
        }

        await transaction.CommitAsync(cancellationToken);

        return ServiceResult<ConsumedSimulationJoinTicketResponse>.Ok(
            new ConsumedSimulationJoinTicketResponse(
                joinTicket.AccountId,
                joinTicket.CharacterId,
                joinTicket.CharacterName,
                joinTicket.ShardId,
                joinTicket.WorldId,
                joinTicket.WorkerId,
                joinTicket.WorkerRuntimeId,
                simulationSession.Id,
                sessionToken,
                simulationSession.ExpiresAt,
                isReconnect));
    }

    private static async Task<PlacementRow?> SelectPlacementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string shardId,
        int heartbeatTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select shard.id as "ShardId",
                   shard.display_name as "ShardDisplayName",
                   shard.world_id as "WorldId",
                   fleet.id as "FleetId",
                   fleet.display_name as "FleetDisplayName",
                   fleet.region_code as "RegionCode",
                   shard.rule_set as "RuleSet",
                   worker.id as "WorkerId",
                   worker.runtime_id as "RuntimeId",
                   worker.host as "Host",
                   worker.udp_port as "UdpPort",
                   worker.max_connections as "Capacity",
                   greatest(worker.active_connections, session_usage.active_players)
                       as "ActivePlayers",
                   worker.protocol_version as "ProtocolVersion",
                   worker.simulation_revision as "SimulationRevision",
                   worker.collision_revision as "CollisionRevision"
            from shards shard
            join fleets fleet on fleet.id = shard.fleet_id and fleet.is_enabled
            join simulation_assignments assignment
              on assignment.shard_id = shard.id
             and assignment.released_at is null
            join simulation_workers worker
              on worker.id = assignment.worker_id
             and worker.is_online
             and worker.last_heartbeat_at
                 + make_interval(secs => @HeartbeatTimeoutSeconds) > now()
            cross join lateral (
                select count(*)::integer as active_players
                from character_simulation_sessions simulation_session
                where simulation_session.simulation_worker_id = worker.id
                  and simulation_session.worker_runtime_id = worker.runtime_id
                  and simulation_session.released_at is null
                  and simulation_session.expires_at > now()
            ) session_usage
            cross join lateral (
                select count(*)::integer as pending_tickets
                from simulation_join_tickets pending_ticket
                where pending_ticket.simulation_worker_id = worker.id
                  and pending_ticket.worker_runtime_id = worker.runtime_id
                  and pending_ticket.consumed_at is null
                  and pending_ticket.expires_at > now()
            ) ticket_usage
            where shard.id = @ShardId
              and shard.is_enabled
              and greatest(worker.active_connections, session_usage.active_players)
                  + ticket_usage.pending_tickets
                  < worker.max_connections
            order by (
                worker.active_connections::numeric
                / greatest(worker.max_connections, 1)) asc,
                worker.id asc
            for update of worker
            limit 1;
            """;

        return await connection.QuerySingleOrDefaultAsync<PlacementRow>(new CommandDefinition(
            sql,
            new { ShardId = shardId, HeartbeatTimeoutSeconds = heartbeatTimeoutSeconds },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<PlacementRow?> SelectReconnectPlacementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActiveSimulationSessionRow session,
        int heartbeatTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select shard.id as "ShardId",
                   shard.display_name as "ShardDisplayName",
                   shard.world_id as "WorldId",
                   fleet.id as "FleetId",
                   fleet.display_name as "FleetDisplayName",
                   fleet.region_code as "RegionCode",
                   shard.rule_set as "RuleSet",
                   worker.id as "WorkerId",
                   worker.runtime_id as "RuntimeId",
                   worker.host as "Host",
                   worker.udp_port as "UdpPort",
                   worker.max_connections as "Capacity",
                   worker.active_connections as "ActivePlayers",
                   worker.protocol_version as "ProtocolVersion",
                   worker.simulation_revision as "SimulationRevision",
                   worker.collision_revision as "CollisionRevision"
            from shards shard
            join fleets fleet on fleet.id = shard.fleet_id and fleet.is_enabled
            join simulation_workers worker
              on worker.id = @WorkerId
             and worker.runtime_id = @WorkerRuntimeId
             and worker.is_online
             and worker.last_heartbeat_at
                 + make_interval(secs => @HeartbeatTimeoutSeconds) > now()
            join simulation_assignments assignment
              on assignment.worker_id = worker.id
             and assignment.shard_id = shard.id
             and assignment.released_at is null
            where shard.id = @ShardId
              and shard.is_enabled
            for update of worker;
            """;

        return await connection.QuerySingleOrDefaultAsync<PlacementRow>(new CommandDefinition(
            sql,
            new
            {
                session.ShardId,
                WorkerId = session.SimulationWorkerId,
                session.WorkerRuntimeId,
                HeartbeatTimeoutSeconds = heartbeatTimeoutSeconds
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<bool> LockEnabledShardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string shardId,
        CancellationToken cancellationToken)
    {
        var lockedShardId = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                select shard.id
                from shards shard
                join fleets fleet on fleet.id = shard.fleet_id
                where shard.id = @ShardId
                  and shard.is_enabled
                  and fleet.is_enabled
                for update of shard;
                """,
                new { ShardId = shardId },
                transaction,
                cancellationToken: cancellationToken));

        return lockedShardId is not null;
    }

    private static async Task<bool> LockOwnedCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var lockedCharacterId = await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                select id
                from characters
                where id = @CharacterId
                  and account_id = @AccountId
                  and deleted_at is null
                for update;
                """,
                new { AccountId = accountId, CharacterId = characterId },
                transaction,
                cancellationToken: cancellationToken));

        return lockedCharacterId is not null;
    }

    private static async Task LockAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "select id from accounts where id = @AccountId for update;",
            new { AccountId = accountId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "select id from characters where id = @CharacterId for update;",
            new { CharacterId = characterId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<SimulationTicketOwnerRow?> FindTicketOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string ticketHash,
        CancellationToken cancellationToken)
    {
        return await connection.QuerySingleOrDefaultAsync<SimulationTicketOwnerRow>(new CommandDefinition(
            """
            select account_id as "AccountId",
                   character_id as "CharacterId"
            from simulation_join_tickets
            where ticket_hash = @TicketHash;
            """,
            new { TicketHash = ticketHash },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<SimulationJoinTicketRow?> GetValidJoinTicketAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string ticketHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select ticket.id as "TicketId",
                   ticket.account_id as "AccountId",
                   ticket.account_session_id as "AccountSessionId",
                   ticket.character_id as "CharacterId",
                   character.name as "CharacterName",
                   ticket.shard_id as "ShardId",
                   shard.world_id as "WorldId",
                   ticket.simulation_worker_id as "WorkerId",
                   ticket.worker_runtime_id as "WorkerRuntimeId",
                   ticket.simulation_session_id as "TargetSimulationSessionId"
            from simulation_join_tickets ticket
            join characters character
              on character.id = ticket.character_id
             and character.deleted_at is null
            join shards shard on shard.id = ticket.shard_id
            where ticket.ticket_hash = @TicketHash
              and ticket.consumed_at is null
              and ticket.expires_at > now()
              and ticket.simulation_worker_id is not null
              and ticket.worker_runtime_id is not null
              and (
                  ticket.account_session_id is null
                  or exists (
                      select 1
                      from account_sessions account_session
                      where account_session.id = ticket.account_session_id
                        and account_session.revoked_at is null
                        and account_session.expires_at > now()))
            for update of ticket;
            """;

        return await connection.QuerySingleOrDefaultAsync<SimulationJoinTicketRow>(
            new CommandDefinition(
                sql,
                new { TicketHash = ticketHash },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task ReleaseExpiredSimulationSessionsForAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update character_simulation_sessions
            set released_at = now()
            where account_id = @AccountId
              and released_at is null
              and expires_at <= now();
            """,
            new { AccountId = accountId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<ActiveSimulationSessionRow?> GetActiveSimulationSessionForAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        return await connection.QuerySingleOrDefaultAsync<ActiveSimulationSessionRow>(
            new CommandDefinition(
                """
                select id as "Id",
                       character_id as "CharacterId",
                       shard_id as "ShardId",
                       simulation_worker_id as "SimulationWorkerId",
                       worker_runtime_id as "WorkerRuntimeId",
                       expires_at as "ExpiresAt"
                from character_simulation_sessions
                where account_id = @AccountId
                  and released_at is null
                for update;
                """,
                new { AccountId = accountId },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task<ActiveSimulationSessionRow> CreateSimulationSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SimulationJoinTicketRow ticket,
        string sessionTokenHash,
        int leaseLifetimeSeconds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into character_simulation_sessions (
                id,
                session_token_hash,
                account_id,
                account_session_id,
                character_id,
                shard_id,
                simulation_worker_id,
                worker_runtime_id,
                expires_at)
            values (
                @Id,
                @SessionTokenHash,
                @AccountId,
                @AccountSessionId,
                @CharacterId,
                @ShardId,
                @WorkerId,
                @WorkerRuntimeId,
                now() + make_interval(secs => @LeaseLifetimeSeconds))
            returning id as "Id",
                      character_id as "CharacterId",
                      shard_id as "ShardId",
                      simulation_worker_id as "SimulationWorkerId",
                      worker_runtime_id as "WorkerRuntimeId",
                      expires_at as "ExpiresAt";
            """;

        return await connection.QuerySingleAsync<ActiveSimulationSessionRow>(new CommandDefinition(
            sql,
            new
            {
                Id = Guid.NewGuid(),
                SessionTokenHash = sessionTokenHash,
                ticket.AccountId,
                ticket.AccountSessionId,
                ticket.CharacterId,
                ticket.ShardId,
                ticket.WorkerId,
                ticket.WorkerRuntimeId,
                LeaseLifetimeSeconds = leaseLifetimeSeconds
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<ActiveSimulationSessionRow> RefreshSimulationSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid simulationSessionId,
        SimulationJoinTicketRow ticket,
        string sessionTokenHash,
        int leaseLifetimeSeconds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update character_simulation_sessions
            set session_token_hash = @SessionTokenHash,
                account_session_id = @AccountSessionId,
                simulation_worker_id = @WorkerId,
                worker_runtime_id = @WorkerRuntimeId,
                last_heartbeat_at = now(),
                expires_at = now() + make_interval(secs => @LeaseLifetimeSeconds)
            where id = @SimulationSessionId
              and released_at is null
            returning id as "Id",
                      character_id as "CharacterId",
                      shard_id as "ShardId",
                      simulation_worker_id as "SimulationWorkerId",
                      worker_runtime_id as "WorkerRuntimeId",
                      expires_at as "ExpiresAt";
            """;

        return await connection.QuerySingleAsync<ActiveSimulationSessionRow>(new CommandDefinition(
            sql,
            new
            {
                SimulationSessionId = simulationSessionId,
                ticket.AccountSessionId,
                ticket.WorkerId,
                ticket.WorkerRuntimeId,
                SessionTokenHash = sessionTokenHash,
                LeaseLifetimeSeconds = leaseLifetimeSeconds
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static ServiceResult<ConsumedSimulationJoinTicketResponse>?
        ValidateConsumeRequest(ConsumeSimulationJoinTicketRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Ticket))
        {
            return ServiceResult<ConsumedSimulationJoinTicketResponse>.BadRequest(
                "invalid_ticket",
                "Join ticket is required.");
        }

        if (!IsValidIdentifier(request.WorkerId)
            || !IsValidIdentifier(request.RuntimeId)
            || !IsValidIdentifier(request.ShardId))
        {
            return ServiceResult<ConsumedSimulationJoinTicketResponse>.BadRequest(
                "invalid_simulation_target",
                "Worker, runtime, and shard ids are required.");
        }

        return null;
    }

    private static ServiceResult<ConsumedSimulationJoinTicketResponse> InvalidTicket()
    {
        return ServiceResult<ConsumedSimulationJoinTicketResponse>.Unauthorized(
            "invalid_ticket",
            "Join ticket is invalid or expired.");
    }

    private static bool IsValidIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private sealed record SimulationJoinTicketRow(
        Guid TicketId,
        Guid AccountId,
        Guid? AccountSessionId,
        Guid CharacterId,
        string CharacterName,
        string ShardId,
        string WorldId,
        string WorkerId,
        string WorkerRuntimeId,
        Guid? TargetSimulationSessionId);

    private sealed record SimulationTicketOwnerRow(Guid AccountId, Guid CharacterId);

    private sealed record ActiveSimulationSessionRow(
        Guid Id,
        Guid CharacterId,
        string ShardId,
        string SimulationWorkerId,
        string WorkerRuntimeId,
        DateTime ExpiresAt);

    private sealed record PlacementRow(
        string ShardId,
        string ShardDisplayName,
        string WorldId,
        string FleetId,
        string FleetDisplayName,
        string RegionCode,
        string RuleSet,
        string WorkerId,
        string RuntimeId,
        string Host,
        int UdpPort,
        int Capacity,
        int ActivePlayers,
        int ProtocolVersion,
        string SimulationRevision,
        string CollisionRevision)
    {
        public ShardResponse ToShardResponse()
        {
            return new ShardResponse(
                ShardId,
                ShardDisplayName,
                WorldId,
                FleetId,
                FleetDisplayName,
                RegionCode,
                RuleSet,
                true,
                ActivePlayers,
                Capacity);
        }

        public SimulationEndpointResponse ToEndpointResponse()
        {
            return new SimulationEndpointResponse(
                WorkerId,
                RuntimeId,
                Host,
                UdpPort,
                ProtocolVersion,
                SimulationRevision,
                CollisionRevision);
        }
    }
}
