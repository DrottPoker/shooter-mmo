using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Placement.Development;
using Mmorpg.GameService.Placement.Integration;
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
    ICharacterSimulationStates simulationStates,
    DevelopmentSimulationBotAuthority developmentBotAuthority,
    IAccountSessionActivity accountSessionActivity,
    PlacementMetrics metrics)
{
    // The most sessions one heartbeat request names.
    public const int MaximumHeartbeatSessions = 500;

    public const string InvalidHeartbeatCode = "invalid_simulation_session_heartbeat";

    private static readonly ErrorResponse InvalidSessionError = new(
        "invalid_simulation_session",
        "Simulation session is invalid, released, expired, or owned by another worker runtime.");

    // Renews the leases of a worker runtime's sessions with a fixed number of statements for the
    // whole batch, as RenewAsync describes. Each session is answered on its own, so a refused one
    // never fails the others, and a session's answer does not depend on the other sessions of the
    // batch. The sessions of development bots are renewed in memory.
    public async Task<ServiceResult<SimulationSessionHeartbeatResponse>> HeartbeatAsync(
        string workerId,
        SimulationSessionHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateHeartbeat(workerId, request);
        if (validationError is not null)
        {
            return validationError;
        }

        var entries = request.Sessions!;
        var trimmedWorkerId = workerId.Trim();
        var runtimeId = request.RuntimeId!.Trim();
        var results = new SimulationSessionHeartbeatResult?[entries.Length];
        var bots = developmentBotAuthority.HeartbeatSessions(trimmedWorkerId, runtimeId, entries);
        var databaseIds = new List<Guid>(entries.Length);
        var databaseTokenHashes = new List<string>(entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (bots.TryGetValue(entry.SimulationSessionId, out var bot))
            {
                results[index] = bot;
                continue;
            }

            databaseIds.Add(entry.SimulationSessionId);
            databaseTokenHashes.Add(TokenGenerator.HashToken(entry.SessionToken!.Trim()));
        }

        var leases = databaseIds.Count == 0
            ? new Dictionary<Guid, SimulationSessionLeaseResponse>()
            : await RenewAsync(trimmedWorkerId, runtimeId, databaseIds, databaseTokenHashes, cancellationToken);
        var answers = new SimulationSessionHeartbeatResult[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            var simulationSessionId = entries[index].SimulationSessionId;
            answers[index] = results[index]
                ?? (leases.TryGetValue(simulationSessionId, out var lease)
                    ? SimulationSessionHeartbeatResult.Renewed(lease)
                    : SimulationSessionHeartbeatResult.Refused(simulationSessionId, InvalidSessionError));
        }

        return ServiceResult<SimulationSessionHeartbeatResponse>.Ok(new SimulationSessionHeartbeatResponse(answers));
    }

    // Renews the leases in a short transaction: one statement locks the sessions and the next
    // renews them and reads how their account sessions ended. After the commit, the account
    // sessions of the renewed sessions are extended and the renewed characters' carry states read.
    // A renewed session whose character has no item state is left out, so it is refused although
    // its lease renewal committed.
    private async Task<Dictionary<Guid, SimulationSessionLeaseResponse>> RenewAsync(
        string workerId,
        string runtimeId,
        List<Guid> simulationSessionIds,
        List<string> sessionTokenHashes,
        CancellationToken cancellationToken)
    {
        var leaseLifetimeSeconds = options.SessionLeaseLifetimeSeconds;

        // Locks the sessions in ascending id order, the order of every statement that locks
        // several sessions, so two heartbeats, a checkpoint, or a worker going offline never
        // deadlock with it. A session that it waited for is checked again in its newest version.
        const string lockSql = """
            select simulation_session.id
            from character_simulation_sessions simulation_session
            join unnest(@SimulationSessionIds::uuid[], @SessionTokenHashes::text[])
                as requested(id, session_token_hash)
              on requested.id = simulation_session.id
             and requested.session_token_hash = simulation_session.session_token_hash
            where simulation_session.simulation_worker_id = @WorkerId
              and simulation_session.worker_runtime_id = @WorkerRuntimeId
              and simulation_session.released_at is null
              and simulation_session.expires_at > now()
            order by simulation_session.id
            for no key update of simulation_session;
            """;

        // Runs after the lock, so it reads what a transaction that the lock waited for committed,
        // such as a new login that replaced the account session, and names that end. The lease
        // goes on after the account session ended, since the character's body stays in the world
        // until the worker releases the session. A live session holds its account session, so the
        // clock never ends it here; an account session that ended without marking its simulation
        // session is marked here, as a logout or a new login marks it.
        var renewSql = $"""
            with refreshed_session as (
                update character_simulation_sessions
                set last_heartbeat_at = clock_timestamp(),
                    expires_at = clock_timestamp() + make_interval(secs => @LeaseLifetimeSeconds),
                    account_session_ended_at = coalesce(
                        account_session_ended_at,
                        case
                            when {SimulationAccountSessionReferences.IsActiveOrUnbound("character_simulation_sessions.account_session_id")} then null
                            else clock_timestamp()
                        end)
                where character_simulation_sessions.id = any(@LockedSessionIds)
                returning character_simulation_sessions.id,
                          character_simulation_sessions.character_id,
                          character_simulation_sessions.shard_id,
                          character_simulation_sessions.simulation_worker_id,
                          character_simulation_sessions.worker_runtime_id,
                          character_simulation_sessions.expires_at,
                          character_simulation_sessions.account_session_id,
                          character_simulation_sessions.account_session_ended_at
            )
            select refreshed.id as "SimulationSessionId",
                   refreshed.character_id as "CharacterId",
                   refreshed.shard_id as "ShardId",
                   refreshed.simulation_worker_id as "WorkerId",
                   refreshed.worker_runtime_id as "WorkerRuntimeId",
                   refreshed.expires_at as "ExpiresAt",
                   {SimulationAccountSessionEndSql.Of("refreshed")} as "AccountSessionEnd",
                   refreshed.account_session_id as "AccountSessionId"
            from refreshed_session refreshed;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        HeartbeatRow[] refreshed;
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var locked = (await connection.QueryAsync<Guid>(new CommandDefinition(
                lockSql,
                new
                {
                    SimulationSessionIds = simulationSessionIds.ToArray(),
                    SessionTokenHashes = sessionTokenHashes.ToArray(),
                    WorkerId = workerId,
                    WorkerRuntimeId = runtimeId
                },
                transaction,
                cancellationToken: cancellationToken))).ToArray();
            if (locked.Length == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return [];
            }

            refreshed = (await connection.QueryAsync<HeartbeatRow>(new CommandDefinition(
                renewSql,
                new
                {
                    LockedSessionIds = locked,
                    LeaseLifetimeSeconds = leaseLifetimeSeconds
                },
                transaction,
                cancellationToken: cancellationToken))).ToArray();
            await transaction.CommitAsync(cancellationToken);
        }

        // Play in the World is activity of the account session. The extension holds no simulation
        // session lock, so it never waits for a login while it holds what the login takes next.
        await accountSessionActivity.ExtendAsync(
            connection,
            refreshed
                .Where(row => row.AccountSessionId is not null && row.AccountSessionEnd is null)
                .Select(row => row.AccountSessionId!.Value)
                .ToArray(),
            cancellationToken);

        // The renewed leases committed first, so the carry states read afterwards are at least as
        // new as the leases.
        var leases = new Dictionary<Guid, SimulationSessionLeaseResponse>(refreshed.Length);
        var carryStates = await characterStateProvider.GetManyAsync(
            connection,
            refreshed.Select(row => row.CharacterId).ToArray(),
            cancellationToken);
        foreach (var row in refreshed)
        {
            if (!carryStates.TryGetValue(row.CharacterId, out var carryState))
            {
                continue;
            }

            leases[row.SimulationSessionId] = row.Lease.ToResponse(carryState, released: false) with
            {
                LeaseDurationMilliseconds = leaseLifetimeSeconds * 1_000L,
                AccountSessionEnded = row.AccountSessionEnd is { } end
                    ? SimulationAccountSessionEndCodes.Of(end)
                    : null
            };
        }

        return leases;
    }

    private static ServiceResult<SimulationSessionHeartbeatResponse>? ValidateHeartbeat(
        string workerId,
        SimulationSessionHeartbeatRequest request)
    {
        if (!Identifiers.IsServiceIdentifier(workerId) || !Identifiers.IsServiceIdentifier(request.RuntimeId))
        {
            return ServiceResult<SimulationSessionHeartbeatResponse>.BadRequest(
                "invalid_simulation_worker_identity",
                "Simulation worker id and runtime id are required.");
        }

        var entries = request.Sessions;
        if (entries is null || entries.Length is 0 or > MaximumHeartbeatSessions)
        {
            return ServiceResult<SimulationSessionHeartbeatResponse>.BadRequest(
                InvalidHeartbeatCode,
                $"A session heartbeat names 1 to {MaximumHeartbeatSessions} simulation sessions.");
        }

        var sessionIds = new HashSet<Guid>();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry is null || entry.SimulationSessionId == Guid.Empty)
            {
                return ServiceResult<SimulationSessionHeartbeatResponse>.BadRequest(
                    "invalid_simulation_session_id",
                    $"Session {index + 1} of the heartbeat names no simulation session.");
            }

            if (string.IsNullOrWhiteSpace(entry.SessionToken))
            {
                return ServiceResult<SimulationSessionHeartbeatResponse>.BadRequest(
                    "invalid_simulation_session_token",
                    $"Session {index + 1} of the heartbeat has no simulation session token.");
            }

            if (!sessionIds.Add(entry.SimulationSessionId))
            {
                return ServiceResult<SimulationSessionHeartbeatResponse>.BadRequest(
                    InvalidHeartbeatCode,
                    $"The session heartbeat names simulation session {entry.SimulationSessionId} more than once.");
            }
        }

        return null;
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

    // How many sessions nothing released although their lease ran out, on a worker runtime that
    // still answers: a live worker renews or releases every session it holds, so each one is a
    // session that it failed to let go of. Sessions of a worker that stopped answering are not
    // counted; their worker's absence says why. One count over the partial index of unreleased
    // sessions, which holds only the sessions whose lease has not been seen to end by a release.
    public async Task<int> CountExpiredUnreleasedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            select count(*)::integer
            from character_simulation_sessions simulation_session
            join simulation_workers worker
              on worker.id = simulation_session.simulation_worker_id
             and worker.runtime_id = simulation_session.worker_runtime_id
            where simulation_session.released_at is null
              and simulation_session.expires_at <= now()
              and worker.is_online
              and worker.last_heartbeat_at + make_interval(secs => @HeartbeatTimeoutSeconds) > now();
            """,
            new { HeartbeatTimeoutSeconds = (int)options.WorkerHeartbeatTimeout.TotalSeconds },
            cancellationToken: cancellationToken));
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
            metrics.RecordCharacterStates(
                PlacementMetrics.CheckpointSource,
                PlacementMetrics.SessionNotActiveOutcome,
                entries.Length);
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
        metrics.RecordCharacterStates(PlacementMetrics.CheckpointSource, PlacementMetrics.SavedOutcome, saved.Count);
        metrics.RecordCharacterStates(
            PlacementMetrics.CheckpointSource,
            PlacementMetrics.NotNewerOutcome,
            writes.Length - saved.Count);
        metrics.RecordCharacterStates(
            PlacementMetrics.CheckpointSource,
            PlacementMetrics.SessionNotActiveOutcome,
            entries.Length - writes.Length);
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
            var finalStateOutcome = PlacementMetrics.AlreadyReleasedOutcome;
            if (row is { EndedByThisRelease: true, WorldId: not null, CollisionRevision: not null })
            {
                var saved = await simulationStates.SaveManyAsync(
                    scope,
                    [row.ToWrite(finalState)],
                    cancellationToken);
                finalStateSaved = saved.Contains(row.CharacterId);
                finalStateOutcome = finalStateSaved ? PlacementMetrics.SavedOutcome : PlacementMetrics.NotNewerOutcome;
            }
            else if (row is { EndedByThisRelease: true })
            {
                finalStateOutcome = PlacementMetrics.SessionNotActiveOutcome;
            }

            await transaction.CommitAsync(cancellationToken);
            if (row is not null)
            {
                metrics.RecordCharacterStates(PlacementMetrics.ReleaseSource, finalStateOutcome, 1);
            }
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

    // A renewed lease with how its account session ended, if it did, and that account session.
    private sealed record HeartbeatRow(
        Guid SimulationSessionId,
        Guid CharacterId,
        string ShardId,
        string WorkerId,
        string WorkerRuntimeId,
        DateTime ExpiresAt,
        SimulationAccountSessionEnd? AccountSessionEnd,
        Guid? AccountSessionId)
    {
        public SessionLeaseRow Lease => new(
            SimulationSessionId,
            CharacterId,
            ShardId,
            WorkerId,
            WorkerRuntimeId,
            ExpiresAt);
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
