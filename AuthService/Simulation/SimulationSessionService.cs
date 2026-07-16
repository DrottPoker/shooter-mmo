using AuthService.Auth;
using AuthService.Http;
using Dapper;
using Npgsql;

namespace AuthService.Simulation;

public sealed class SimulationSessionService(
    NpgsqlDataSource dataSource,
    IConfiguration configuration)
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
        var leaseLifetimeSeconds = configuration.GetValue(
            "Simulation:SessionLeaseLifetimeSeconds",
            30);

        const string sql = """
            with refreshed_session as (
                update character_simulation_sessions
                set last_heartbeat_at = now(),
                    expires_at = now() + make_interval(secs => @LeaseLifetimeSeconds)
                where id = @SimulationSessionId
                  and simulation_worker_id = @WorkerId
                  and worker_runtime_id = @WorkerRuntimeId
                  and session_token_hash = @SessionTokenHash
                  and released_at is null
                  and expires_at > now()
                  and (
                      account_session_id is null
                      or exists (
                          select 1
                          from account_sessions
                          where id = character_simulation_sessions.account_session_id
                            and revoked_at is null
                            and expires_at > now()))
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
                   refreshed.expires_at as "ExpiresAt",
                   item_state.revision as "ItemStateRevision",
                   item_state.carried_weight as "CarriedWeight",
                   item_state.carry_capacity as "CarryCapacity",
                   false as "Released"
            from refreshed_session refreshed
            join character_item_states item_state
              on item_state.character_id = refreshed.character_id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var session = await connection.QuerySingleOrDefaultAsync<SimulationSessionLeaseResponse>(
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
        if (session is not null)
        {
            return ServiceResult<SimulationSessionLeaseResponse>.Ok(session);
        }

        const string revocationReasonSql = """
            select account_session.revocation_reason
            from character_simulation_sessions simulation_session
            left join account_sessions account_session
              on account_session.id = simulation_session.account_session_id
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
                   released.expires_at as "ExpiresAt",
                   item_state.revision as "ItemStateRevision",
                   item_state.carried_weight as "CarriedWeight",
                   item_state.carry_capacity as "CarryCapacity",
                   true as "Released"
            from released_session released
            join character_item_states item_state
              on item_state.character_id = released.character_id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var session = await connection.QuerySingleOrDefaultAsync<SimulationSessionLeaseResponse>(
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

        return session is null
            ? ServiceResult<SimulationSessionLeaseResponse>.Unauthorized(
                "invalid_simulation_session",
                "Simulation session credentials are invalid.")
            : ServiceResult<SimulationSessionLeaseResponse>.Ok(session);
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

        if (!IsValidIdentifier(workerId) || !IsValidIdentifier(request.WorkerRuntimeId))
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

    private static bool IsValidIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}
