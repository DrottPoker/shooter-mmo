using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.Server.Common.Identity;
using Npgsql;

namespace Mmorpg.GameService.Placement;

internal sealed class SimulationSessionService(
    NpgsqlDataSource dataSource,
    PlacementModuleOptions options,
    ISimulationCharacterStateProvider characterStateProvider)
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

    public async Task<ServiceResult<SimulationSessionLeaseResponse>> ReleaseAsync(
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
                    WorkerRuntimeId = request.WorkerRuntimeId!.Trim(),
                    SessionTokenHash = sessionTokenHash
                },
                cancellationToken: cancellationToken));
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
                released.ToResponse(carryState, released: true));
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
                released);
        }
    }
}
