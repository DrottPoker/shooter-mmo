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

    public async Task<(Guid SessionId, string Token, DateTime ExpiresAt, int ReplacedSessionCount)>
        ReplaceSessionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid accountId,
            CancellationToken cancellationToken)
    {
        const string lockAccountSql = """
            select id
            from accounts
            where id = @AccountId
            for update;
            """;

        await connection.QuerySingleAsync<Guid>(new CommandDefinition(
            lockAccountSql,
            new { AccountId = accountId },
            transaction,
            cancellationToken: cancellationToken));

        const string revokeSessionsSql = """
            update account_sessions
            set revoked_at = now(),
                revocation_reason = @RevocationReason
            where account_id = @AccountId
              and revoked_at is null;
            """;

        var replacedSessionCount = await connection.ExecuteAsync(new CommandDefinition(
            revokeSessionsSql,
            new
            {
                AccountId = accountId,
                RevocationReason = AccountSessionRevocationReason.SessionReplaced
            },
            transaction,
            cancellationToken: cancellationToken));

        const string releaseSimulationSessionsSql = """
            update character_simulation_sessions
            set released_at = coalesce(released_at, now())
            where account_id = @AccountId
              and released_at is null;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            releaseSimulationSessionsSql,
            new { AccountId = accountId },
            transaction,
            cancellationToken: cancellationToken));

        const string revokeTicketsSql = """
            update simulation_join_tickets
            set consumed_at = now()
            where account_id = @AccountId
              and consumed_at is null;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            revokeTicketsSql,
            new { AccountId = accountId },
            transaction,
            cancellationToken: cancellationToken));

        var session = await CreateSessionAsync(
            connection,
            transaction,
            accountId,
            cancellationToken);

        return (
            session.SessionId,
            session.Token,
            session.ExpiresAt,
            replacedSessionCount);
    }

    public async Task<AuthenticatedAccount?> AuthenticateTokenAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var result = await ResolveTokenAsync(token, cancellationToken);
        return result.Account;
    }

    public async Task<AccountSessionAuthenticationResult> ResolveTokenAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var tokenHash = TokenGenerator.HashToken(token);

        const string sql = """
            select a.id as "AccountId",
                   a.username as "Username",
                   s.id as "SessionId",
                   s.expires_at <= now() as "IsExpired",
                   s.revoked_at as "RevokedAt",
                   s.revocation_reason as "RevocationReason"
            from account_sessions s
            join accounts a on a.id = s.account_id
            where s.token_hash = @TokenHash;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var session = await connection.QuerySingleOrDefaultAsync<AccountSessionAuthenticationRow>(
            new CommandDefinition(sql, new { TokenHash = tokenHash }, cancellationToken: cancellationToken));

        if (session is null)
        {
            return AccountSessionAuthenticationResult.Invalid();
        }

        if (session.RevokedAt is not null)
        {
            return string.Equals(
                session.RevocationReason,
                AccountSessionRevocationReason.SessionReplaced,
                StringComparison.Ordinal)
                ? AccountSessionAuthenticationResult.Replaced()
                : AccountSessionAuthenticationResult.Invalid();
        }

        if (session.IsExpired)
        {
            return AccountSessionAuthenticationResult.Invalid();
        }

        return AccountSessionAuthenticationResult.Success(new AuthenticatedAccount(
            session.AccountId,
            session.Username,
            session.SessionId));
    }

    public async Task RevokeAsync(
        Guid accountId,
        Guid sessionId,
        string revocationReason,
        CancellationToken cancellationToken)
    {
        if (revocationReason is not (
            AccountSessionRevocationReason.Logout
            or AccountSessionRevocationReason.ManualRevoke))
        {
            throw new ArgumentException("Revocation reason is invalid.", nameof(revocationReason));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string revokeSessionSql = """
            update account_sessions
            set revoked_at = coalesce(revoked_at, now()),
                revocation_reason = coalesce(revocation_reason, @RevocationReason)
            where id = @SessionId and account_id = @AccountId;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            revokeSessionSql,
            new
            {
                AccountId = accountId,
                SessionId = sessionId,
                RevocationReason = revocationReason
            },
            transaction,
            cancellationToken: cancellationToken));

        const string releaseSimulationSessionsSql = """
            update character_simulation_sessions
            set released_at = coalesce(released_at, now())
            where account_session_id = @SessionId and released_at is null;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            releaseSimulationSessionsSql,
            new { SessionId = sessionId },
            transaction,
            cancellationToken: cancellationToken));

        const string revokeTicketsSql = """
            update simulation_join_tickets
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

    private sealed record AccountSessionAuthenticationRow(
        Guid AccountId,
        string Username,
        Guid SessionId,
        bool IsExpired,
        DateTime? RevokedAt,
        string? RevocationReason);
}

public sealed record AccountSessionAuthenticationResult(
    AuthenticatedAccount? Account,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static AccountSessionAuthenticationResult Success(AuthenticatedAccount account)
    {
        return new AccountSessionAuthenticationResult(account, null, null);
    }

    public static AccountSessionAuthenticationResult Invalid()
    {
        return new AccountSessionAuthenticationResult(
            null,
            "invalid_session_token",
            "Session token is invalid, revoked, or expired.");
    }

    public static AccountSessionAuthenticationResult Replaced()
    {
        return new AccountSessionAuthenticationResult(
            null,
            AccountSessionErrorCode.SessionReplaced,
            "This account logged in from another client.");
    }
}
