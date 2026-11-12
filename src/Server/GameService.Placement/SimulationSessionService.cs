using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.Server.Common.Identity;
using Npgsql;

namespace Mmorpg.GameService.Placement;

internal sealed class SimulationSessionService(
    NpgsqlDataSource dataSource,
    PlacementModuleOptions options,
    ISimulationCharacterStateProvider characterStateProvider,
    ICharacterSimulationStates simulationStates)
{
    public async Task<ServiceResult<SimulationSessionLeaseResponse>> HeartbeatAsync(
        Guid simulationSessionId,
        string workerId,
        SimulationSessionCredentialRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateRequest(simulationSessionId, workerId, request);
        if (validationError is not null)
        {
            return validationError;
        }

        var sessionTokenHash = TokenGenerator.HashToken(request.SessionToken!.Trim());
        var leaseLifetimeSeconds = options.SessionLeaseLifetimeSeconds;

        var sql = $"""
            with refreshed_session as (
                update character_simulation_sessions
                set last_heartbeat_at = clock_timestamp(),
                    expires_at = clock_timestamp() + make_interval(secs => @LeaseLifetimeSeconds)
                where id = @SimulationSessionId
                  and simulation_worker_id = @WorkerId
                  and worker_runtime_id = @WorkerRuntimeId
                  and session_token_hash = @SessionTokenHash
                  and released_at is null
                  and expires_at > now()
                  and {AccountsSql.IsActiveOrUnboundSession("character_simulation_sessions.account_session_id")}
                returning id,
                          character_id,
                          shard_id,
                          simulation_worker_id,
                          worker_runtime_id,
                          expires_at
            )
            select refreshed.id as "SimulationSessionId",
                   refreshed.character_id as "CharacterId",
                   refreshed.shard_id as "ShardId",
                   refreshed.simulation_worker_id as "WorkerId",
                   refreshed.worker_runtime_id as "WorkerRuntimeId",
                   refreshed.expires_at as "ExpiresAt"
            from refreshed_session refreshed;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var refreshed = await connection.QuerySingleOrDefaultAsync<SessionLeaseRow>(
            new CommandDefinition(
                sql,
                new
                {
                    SimulationSessionId = simulationSessionId,
                    WorkerId = workerId.Trim(),
                    WorkerRuntimeId = request.WorkerRuntimeId!.Trim(),
                    SessionTokenHash = sessionTokenHash,
                    LeaseLifetimeSeconds = leaseLifetimeSeconds
                },
                cancellationToken: cancellationToken));
        // The refreshed lease commits on its own, so the carry state read afterwards is
        // at least as new as the lease.
        var carryState = refreshed is null
            ? null
            : await characterStateProvider.GetAsync(
                connection,
                refreshed.CharacterId,
                cancellationToken);
        if (refreshed is not null && carryState is not null)
        {
            return ServiceResult<SimulationSessionLeaseResponse>.Ok(
                refreshed.ToResponse(carryState, released: false) with
                {
                    LeaseDurationMilliseconds = leaseLifetimeSeconds * 1_000L
                });
        }

        var revocationReasonSql = $"""
            select {AccountsSql.RevocationReasonOf("simulation_session.account_session_id")}
            from character_simulation_sessions simulation_session
            where simulation_session.id = @SimulationSessionId
              and simulation_session.simulation_worker_id = @WorkerId
              and simulation_session.worker_runtime_id = @WorkerRuntimeId
              and simulation_session.session_token_hash = @SessionTokenHash;
            """;

        var revocationReason = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                revocationReasonSql,
                new
                {
                    SimulationSessionId = simulationSessionId,
                    WorkerId = workerId.Trim(),
                    WorkerRuntimeId = request.WorkerRuntimeId!.Trim(),
                    SessionTokenHash = sessionTokenHash
                },
                cancellationToken: cancellationToken));

        return string.Equals(
            revocationReason,
            AccountSessionRevocationReason.SessionReplaced,
            StringComparison.Ordinal)
            ? ServiceResult<SimulationSessionLeaseResponse>.Unauthorized(
                AccountSessionErrorCode.SessionReplaced,
                "This account logged in from another client.")
            : ServiceResult<SimulationSessionLeaseResponse>.Unauthorized(
                "invalid_simulation_session",
                "Simulation session is invalid, released, expired, or owned by another worker runtime.");
    }

    // Releases the exact session generation, idempotently. A final state is saved in the same
    // transaction, but only when this release ended the session, so a repeated release or the
    // release of a session that something else ended never writes.
    public async Task<ServiceResult<SimulationSessionLeaseResponse>> ReleaseAsync(
        Guid simulationSessionId,
        string workerId,
        SimulationSessionReleaseRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateRequest(simulationSessionId, workerId, request.Credentials);
        if (validationError is not null)
        {
            return validationError;
        }

        if (request.FinalState is not null
            && SimulationCharacterStateRules.Validate(request.FinalState, "The final character state") is { } stateError)
        {
            return SimulationCharacterStateRules.Invalid<SimulationSessionLeaseResponse>(stateError);
        }

        var sessionTokenHash = TokenGenerator.HashToken(request.SessionToken!.Trim());
        var workerRuntimeId = request.WorkerRuntimeId!.Trim();
        if (request.FinalState is not null)
        {
            return await ReleaseWithFinalStateAsync(
                simulationSessionId,
                workerId.Trim(),
                workerRuntimeId,
                sessionTokenHash,
                request.FinalState,
                cancellationToken);
        }

        const string sql = """
            with released_session as (
                update character_simulation_sessions
                set released_at = coalesce(released_at, now())
                where id = @SimulationSessionId
                  and simulation_worker_id = @WorkerId
                  and worker_runtime_id = @WorkerRuntimeId
                  and session_token_hash = @SessionTokenHash
                returning id,
                          character_id,
                          shard_id,
                          simulation_worker_id,
                          worker_runtime_id,
                          expires_at
            )
            select released.id as "SimulationSessionId",
                   released.character_id as "CharacterId",
                   released.shard_id as "ShardId",
                   released.simulation_worker_id as "WorkerId",
                   released.worker_runtime_id as "WorkerRuntimeId",
                   released.expires_at as "ExpiresAt"
            from released_session released;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var released = await connection.QuerySingleOrDefaultAsync<SessionLeaseRow>(
            new CommandDefinition(
                sql,
                new
                {
                    SimulationSessionId = simulationSessionId,
                    WorkerId = workerId.Trim(),
                    WorkerRuntimeId = workerRuntimeId,
                    SessionTokenHash = sessionTokenHash
                },
                cancellationToken: cancellationToken));
        return await ReleasedResponseAsync(connection, released, finalStateSaved: false, cancellationToken);
    }

    // Saves the character states of a worker runtime's sessions in one batch. A state is saved
    // only for a session that is its character's active, unreleased session on that worker and
    // runtime, and only when it is newer than the stored state of the same session; any other
    // entry is skipped, so a late checkpoint never overwrites a newer state and a released
    // session never writes.
    public async Task<ServiceResult<SimulationCharacterCheckpointResponse>> CheckpointAsync(
        string workerId,
        SimulationCharacterCheckpointRequest request,
        CancellationToken cancellationToken)
    {
        if (!Identifiers.IsServiceIdentifier(workerId) || !Identifiers.IsServiceIdentifier(request.RuntimeId))
        {
            return ServiceResult<SimulationCharacterCheckpointResponse>.BadRequest(
                "invalid_simulation_worker_identity",
                "Simulation worker id and runtime id are required.");
        }

        var entries = request.States;
        if (entries is null || entries.Length is 0 or > SimulationCharacterStateRules.MaximumCheckpointStates)
        {
            return ServiceResult<SimulationCharacterCheckpointResponse>.BadRequest(
                SimulationCharacterStateRules.InvalidCheckpointCode,
                $"A checkpoint names 1 to {SimulationCharacterStateRules.MaximumCheckpointStates} character states.");
        }

        var sessionIds = new HashSet<Guid>();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry is null || entry.SimulationSessionId == Guid.Empty)
            {
                return ServiceResult<SimulationCharacterCheckpointResponse>.BadRequest(
                    SimulationCharacterStateRules.InvalidCheckpointCode,
                    $"Character state {index + 1} of the checkpoint names no simulation session.");
            }

            if (!sessionIds.Add(entry.SimulationSessionId))
            {
                return ServiceResult<SimulationCharacterCheckpointResponse>.BadRequest(
                    SimulationCharacterStateRules.InvalidCheckpointCode,
                    $"The checkpoint names simulation session {entry.SimulationSessionId} more than once.");
            }

            if (SimulationCharacterStateRules.Validate(entry.State, $"Character state {index + 1} of the checkpoint") is { } stateError)
            {
                return SimulationCharacterStateRules.Invalid<SimulationCharacterCheckpointResponse>(stateError);
            }
        }

        var trimmedWorkerId = workerId.Trim();
        var runtimeId = request.RuntimeId.Trim();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var scope = new PostgresTransactionScope(connection, transaction);

        // The characters are locked before their sessions, as the lock order requires, so the
        // candidates are read without a lock first and read again under the session locks.
        var candidates = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            select simulation_session.character_id
            from character_simulation_sessions simulation_session
            where simulation_session.id = any(@SimulationSessionIds)
              and simulation_session.simulation_worker_id = @WorkerId
              and simulation_session.worker_runtime_id = @WorkerRuntimeId
              and simulation_session.released_at is null
              and simulation_session.expires_at > now();
            """,
            new
            {
                SimulationSessionIds = sessionIds.ToArray(),
                WorkerId = trimmedWorkerId,
                WorkerRuntimeId = runtimeId
            },
            transaction,
            cancellationToken: cancellationToken));
        var candidateCharacterIds = candidates.ToArray();
        if (candidateCharacterIds.Length == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<SimulationCharacterCheckpointResponse>.Ok(
                new SimulationCharacterCheckpointResponse(0, entries.Length));
        }

        await simulationStates.LockForSaveAsync(scope, candidateCharacterIds, cancellationToken);
        var activeSessions = await connection.QueryAsync<ActiveSessionRow>(new CommandDefinition(
            """
            select simulation_session.id as "SimulationSessionId",
                   simulation_session.character_id as "CharacterId",
                   shard.world_id as "WorldId",
                   worker.collision_revision as "CollisionRevision"
            from character_simulation_sessions simulation_session
            join shards shard on shard.id = simulation_session.shard_id
            join simulation_workers worker
              on worker.id = simulation_session.simulation_worker_id
             and worker.runtime_id = simulation_session.worker_runtime_id
            where simulation_session.id = any(@SimulationSessionIds)
              and simulation_session.simulation_worker_id = @WorkerId
              and simulation_session.worker_runtime_id = @WorkerRuntimeId
              and simulation_session.released_at is null
              and simulation_session.expires_at > now()
              and shard.world_id is not null
              and worker.collision_revision is not null
            order by simulation_session.id
            for share of simulation_session;
            """,
            new
            {
                SimulationSessionIds = sessionIds.ToArray(),
                WorkerId = trimmedWorkerId,
                WorkerRuntimeId = runtimeId
            },
            transaction,
            cancellationToken: cancellationToken));
        var entriesBySession = entries.ToDictionary(entry => entry.SimulationSessionId);
        var writes = activeSessions
            .Select(session => session.ToWrite(entriesBySession[session.SimulationSessionId].State))
            .ToArray();
        var saved = await simulationStates.SaveManyAsync(scope, writes, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<SimulationCharacterCheckpointResponse>.Ok(
            new SimulationCharacterCheckpointResponse(saved.Count, entries.Length - saved.Count));
    }

    private async Task<ServiceResult<SimulationSessionLeaseResponse>> ReleaseWithFinalStateAsync(
        Guid simulationSessionId,
        string workerId,
        string workerRuntimeId,
        string sessionTokenHash,
        SimulationCharacterStateRequest finalState,
        CancellationToken cancellationToken)
    {
        var parameters = new
        {
            SimulationSessionId = simulationSessionId,
            WorkerId = workerId,
            WorkerRuntimeId = workerRuntimeId,
            SessionTokenHash = sessionTokenHash
        };
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        SessionLeaseRow? released;
        var finalStateSaved = false;
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var scope = new PostgresTransactionScope(connection, transaction);

            // The character is locked before its session, as the lock order requires.
            var characterId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                """
                select character_id
                from character_simulation_sessions
                where id = @SimulationSessionId
                  and simulation_worker_id = @WorkerId
                  and worker_runtime_id = @WorkerRuntimeId
                  and session_token_hash = @SessionTokenHash;
                """,
                parameters,
                transaction,
                cancellationToken: cancellationToken));
            if (characterId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await ReleasedResponseAsync(connection, null, finalStateSaved: false, cancellationToken);
            }

            await simulationStates.LockForSaveAsync(scope, [characterId.Value], cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<ReleasedSessionRow>(new CommandDefinition(
                """
                update character_simulation_sessions simulation_session
                set released_at = coalesce(simulation_session.released_at, now())
                from (
                    select id, released_at
                    from character_simulation_sessions
                    where id = @SimulationSessionId
                      and simulation_worker_id = @WorkerId
                      and worker_runtime_id = @WorkerRuntimeId
                      and session_token_hash = @SessionTokenHash
                    for no key update
                ) previous
                where simulation_session.id = previous.id
                returning simulation_session.id as "SimulationSessionId",
                          simulation_session.character_id as "CharacterId",
                          simulation_session.shard_id as "ShardId",
                          simulation_session.simulation_worker_id as "WorkerId",
                          simulation_session.worker_runtime_id as "WorkerRuntimeId",
                          simulation_session.expires_at as "ExpiresAt",
                          previous.released_at is null as "EndedByThisRelease",
                          (select shard.world_id
                           from shards shard
                           where shard.id = simulation_session.shard_id) as "WorldId",
                          (select worker.collision_revision
                           from simulation_workers worker
                           where worker.id = simulation_session.simulation_worker_id
                             and worker.runtime_id = simulation_session.worker_runtime_id) as "CollisionRevision";
                """,
                parameters,
                transaction,
                cancellationToken: cancellationToken));
            released = row?.Lease;
            if (row is { EndedByThisRelease: true, WorldId: not null, CollisionRevision: not null })
            {
                var saved = await simulationStates.SaveManyAsync(
                    scope,
                    [row.ToWrite(finalState)],
                    cancellationToken);
                finalStateSaved = saved.Contains(row.CharacterId);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return await ReleasedResponseAsync(connection, released, finalStateSaved, cancellationToken);
    }

    // The carry state is read after the release committed, as for a release without a state.
    private async Task<ServiceResult<SimulationSessionLeaseResponse>> ReleasedResponseAsync(
        NpgsqlConnection connection,
        SessionLeaseRow? released,
        bool finalStateSaved,
        CancellationToken cancellationToken)
    {
        var carryState = released is null
            ? null
            : await characterStateProvider.GetAsync(
                connection,
                released.CharacterId,
                cancellationToken);

        return released is null || carryState is null
            ? ServiceResult<SimulationSessionLeaseResponse>.Unauthorized(
                "invalid_simulation_session",
                "Simulation session credentials are invalid.")
            : ServiceResult<SimulationSessionLeaseResponse>.Ok(
                released.ToResponse(carryState, released: true) with
                {
                    FinalStateSaved = finalStateSaved
                });
    }

    private static CharacterSimulationStateWrite Write(
        Guid characterId,
        Guid simulationSessionId,
        string worldId,
        string collisionRevision,
        SimulationCharacterStateRequest state)
    {
        return new CharacterSimulationStateWrite(
            characterId,
            simulationSessionId,
            state.Sequence,
            new CharacterSimulationState(
                worldId,
                collisionRevision,
                state.PositionX,
                state.PositionY,
                state.PositionZ,
                state.YawDegrees,
                state.Health,
                state.Mana));
    }

    private static ServiceResult<SimulationSessionLeaseResponse>? ValidateRequest(
        Guid simulationSessionId,
        string workerId,
        SimulationSessionCredentialRequest request)
    {
        if (simulationSessionId == Guid.Empty)
        {
            return ServiceResult<SimulationSessionLeaseResponse>.BadRequest(
                "invalid_simulation_session_id",
                "Simulation session id is required.");
        }

        if (!Identifiers.IsServiceIdentifier(workerId) || !Identifiers.IsServiceIdentifier(request.WorkerRuntimeId))
        {
            return ServiceResult<SimulationSessionLeaseResponse>.BadRequest(
                "invalid_simulation_worker_identity",
                "Simulation worker id and runtime id are required.");
        }

        if (string.IsNullOrWhiteSpace(request.SessionToken))
        {
            return ServiceResult<SimulationSessionLeaseResponse>.BadRequest(
                "invalid_simulation_session_token",
                "Simulation session token is required.");
        }

        return null;
    }

    private sealed record SessionLeaseRow(
        Guid SimulationSessionId,
        Guid CharacterId,
        string ShardId,
        string WorkerId,
        string WorkerRuntimeId,
        DateTime ExpiresAt)
    {
        public SimulationSessionLeaseResponse ToResponse(
            SimulationCharacterItemState carryState,
            bool released)
        {
            return new SimulationSessionLeaseResponse(
                SimulationSessionId,
                CharacterId,
                ShardId,
                WorkerId,
                WorkerRuntimeId,
                ExpiresAt,
                carryState.ItemStateRevision,
                carryState.CarriedWeight,
                carryState.CarryCapacity,
                released)
            {
                Equipment = carryState.Equipment
            };
        }
    }

    // A released session, whether this release ended it, and the World and collision revision
    // its worker runtime simulated.
    private sealed record ReleasedSessionRow(
        Guid SimulationSessionId,
        Guid CharacterId,
        string ShardId,
        string WorkerId,
        string WorkerRuntimeId,
        DateTime ExpiresAt,
        bool EndedByThisRelease,
        string? WorldId,
        string? CollisionRevision)
    {
        public SessionLeaseRow Lease => new(
            SimulationSessionId,
            CharacterId,
            ShardId,
            WorkerId,
            WorkerRuntimeId,
            ExpiresAt);

        public CharacterSimulationStateWrite ToWrite(SimulationCharacterStateRequest state)
        {
            return Write(CharacterId, SimulationSessionId, WorldId!, CollisionRevision!, state);
        }
    }

    // An active session of the checkpointing worker runtime with the World and collision
    // revision it simulates.
    private sealed record ActiveSessionRow(
        Guid SimulationSessionId,
        Guid CharacterId,
        string WorldId,
        string CollisionRevision)
    {
        public CharacterSimulationStateWrite ToWrite(SimulationCharacterStateRequest state)
        {
            return Write(CharacterId, SimulationSessionId, WorldId, CollisionRevision, state);
        }
    }
}
