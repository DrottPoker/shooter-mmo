using AuthService.Http;
using Dapper;
using Npgsql;

namespace AuthService.Auth;

public sealed class SessionService(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    public async Task<(Guid SessionId, string Token, DateTime ExpiresAt)> CreateSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var token = TokenGenerator.CreateToken();
        var tokenHash = TokenGenerator.HashToken(token);
        var expiresAt = DateTime.UtcNow.AddHours(
            configuration.GetValue("Auth:SessionLifetimeHours", 24));

        var sessionId = Guid.NewGuid();

        const string sql = """
            insert into account_sessions (id, account_id, token_hash, expires_at)
            values (@Id, @AccountId, @TokenHash, @ExpiresAt);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = sessionId,
                AccountId = accountId,
                TokenHash = tokenHash,
                ExpiresAt = expiresAt
            },
            transaction,
            cancellationToken: cancellationToken));

        return (sessionId, token, expiresAt);
    }

    public async Task<AuthenticatedAccount?> AuthenticateTokenAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var tokenHash = TokenGenerator.HashToken(token);

        const string sql = """
            select a.id as "AccountId", a.username as "Username", s.id as "SessionId"
            from account_sessions s
            join accounts a on a.id = s.account_id
            where s.token_hash = @TokenHash
              and s.revoked_at is null
              and s.expires_at > now();
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<AuthenticatedAccount>(
            new CommandDefinition(sql, new { TokenHash = tokenHash }, cancellationToken: cancellationToken));
    }

    public async Task RevokeAsync(
        Guid accountId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string revokeSessionSql = """
            update account_sessions
            set revoked_at = coalesce(revoked_at, now())
            where id = @SessionId and account_id = @AccountId;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            revokeSessionSql,
            new { AccountId = accountId, SessionId = sessionId },
            transaction,
            cancellationToken: cancellationToken));

        const string releaseWorldSessionsSql = """
            update character_world_sessions
            set released_at = coalesce(released_at, now())
            where account_session_id = @SessionId and released_at is null;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            releaseWorldSessionsSql,
            new { SessionId = sessionId },
            transaction,
            cancellationToken: cancellationToken));

        const string revokeTicketsSql = """
            update world_join_tickets
            set consumed_at = now()
            where account_session_id = @SessionId and consumed_at is null;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            revokeTicketsSql,
            new { SessionId = sessionId },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
    }

    public static string? ReadBearerToken(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var values))
        {
            return null;
        }

        var header = values.ToString();
        const string prefix = "Bearer ";

        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
