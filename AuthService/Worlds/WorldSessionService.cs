using AuthService.Auth;
using AuthService.Http;
using Dapper;
using Npgsql;

namespace AuthService.Worlds;

public sealed class WorldSessionService(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    public async Task<ServiceResult<WorldSessionLeaseResponse>> HeartbeatAsync(
        Guid worldSessionId,
        WorldSessionCredentialRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateRequest(worldSessionId, request);
        if (validationError is not null)
        {
            return validationError;
        }

        var sessionTokenHash = TokenGenerator.HashToken(request.SessionToken!.Trim());
        var leaseLifetimeSeconds = configuration.GetValue("WorldSession:LeaseLifetimeSeconds", 30);

        const string sql = """
            update character_world_sessions
            set last_heartbeat_at = now(),
                expires_at = now() + make_interval(secs => @LeaseLifetimeSeconds)
            where id = @WorldSessionId
              and world_id = @WorldId
              and session_token_hash = @SessionTokenHash
              and released_at is null
              and expires_at > now()
              and (
                  account_session_id is null
                  or exists (
                      select 1
                      from account_sessions
                      where id = character_world_sessions.account_session_id
                        and revoked_at is null
                        and expires_at > now()))
            returning id as "WorldSessionId",
                      character_id as "CharacterId",
                      world_id as "WorldId",
                      expires_at as "ExpiresAt",
                      false as "Released";
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var session = await connection.QuerySingleOrDefaultAsync<WorldSessionLeaseResponse>(
            new CommandDefinition(
                sql,
                new
                {
                    WorldSessionId = worldSessionId,
                    WorldId = request.WorldId!.Trim(),
                    SessionTokenHash = sessionTokenHash,
                    LeaseLifetimeSeconds = leaseLifetimeSeconds
                },
                cancellationToken: cancellationToken));

        if (session is not null)
        {
            return ServiceResult<WorldSessionLeaseResponse>.Ok(session);
        }

        const string revocationReasonSql = """
            select account_sessions.revocation_reason
            from character_world_sessions
            left join account_sessions
              on account_sessions.id = character_world_sessions.account_session_id
            where character_world_sessions.id = @WorldSessionId
              and character_world_sessions.world_id = @WorldId
              and character_world_sessions.session_token_hash = @SessionTokenHash;
            """;

        var revocationReason = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                revocationReasonSql,
                new
                {
                    WorldSessionId = worldSessionId,
                    WorldId = request.WorldId!.Trim(),
                    SessionTokenHash = sessionTokenHash
                },
                cancellationToken: cancellationToken));

        return string.Equals(
            revocationReason,
            AccountSessionRevocationReason.SessionReplaced,
            StringComparison.Ordinal)
            ? ServiceResult<WorldSessionLeaseResponse>.Unauthorized(
                AccountSessionErrorCode.SessionReplaced,
                "This account logged in from another client.")
            : ServiceResult<WorldSessionLeaseResponse>.Unauthorized(
                "invalid_world_session",
                "World session is invalid, released, or expired.");
    }

    public async Task<ServiceResult<WorldSessionLeaseResponse>> ReleaseAsync(
        Guid worldSessionId,
        WorldSessionCredentialRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateRequest(worldSessionId, request);
        if (validationError is not null)
        {
            return validationError;
        }

        var sessionTokenHash = TokenGenerator.HashToken(request.SessionToken!.Trim());

        const string sql = """
            update character_world_sessions
            set released_at = coalesce(released_at, now())
            where id = @WorldSessionId
              and world_id = @WorldId
              and session_token_hash = @SessionTokenHash
            returning id as "WorldSessionId",
                      character_id as "CharacterId",
                      world_id as "WorldId",
                      expires_at as "ExpiresAt",
                      true as "Released";
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var session = await connection.QuerySingleOrDefaultAsync<WorldSessionLeaseResponse>(
            new CommandDefinition(
                sql,
                new
                {
                    WorldSessionId = worldSessionId,
                    WorldId = request.WorldId!.Trim(),
                    SessionTokenHash = sessionTokenHash
                },
                cancellationToken: cancellationToken));

        return session is null
            ? ServiceResult<WorldSessionLeaseResponse>.Unauthorized(
                "invalid_world_session",
                "World session credentials are invalid.")
            : ServiceResult<WorldSessionLeaseResponse>.Ok(session);
    }

    private static ServiceResult<WorldSessionLeaseResponse>? ValidateRequest(
        Guid worldSessionId,
        WorldSessionCredentialRequest request)
    {
        if (worldSessionId == Guid.Empty)
        {
            return ServiceResult<WorldSessionLeaseResponse>.BadRequest(
                "invalid_world_session_id",
                "World session id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.WorldId))
        {
            return ServiceResult<WorldSessionLeaseResponse>.BadRequest(
                "invalid_world_id",
                "World id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.SessionToken))
        {
            return ServiceResult<WorldSessionLeaseResponse>.BadRequest(
                "invalid_world_session_token",
                "World session token is required.");
        }

        return null;
    }
}
