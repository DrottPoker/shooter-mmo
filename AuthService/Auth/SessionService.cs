using Dapper;
using Npgsql;
using AuthService.Http;

namespace AuthService.Auth;

public sealed class SessionService(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    public async Task<(string Token, DateTime ExpiresAt)> CreateSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var token = TokenGenerator.CreateToken();
        var tokenHash = TokenGenerator.HashToken(token);
        var expiresAt = DateTime.UtcNow.AddHours(
            configuration.GetValue("Auth:SessionLifetimeHours", 24));

        const string sql = """
            insert into account_sessions (id, account_id, token_hash, expires_at)
            values (@Id, @AccountId, @TokenHash, @ExpiresAt);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TokenHash = tokenHash,
                ExpiresAt = expiresAt
            },
            transaction,
            cancellationToken: cancellationToken));

        return (token, expiresAt);
    }

    public async Task<ServiceResult<AuthenticatedAccount>> AuthenticateAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var token = ReadBearerToken(request);

        if (token is null)
        {
            return ServiceResult<AuthenticatedAccount>.Unauthorized(
                "missing_session_token",
                "A bearer session token is required.");
        }

        var tokenHash = TokenGenerator.HashToken(token);

        const string sql = """
            select a.id as "AccountId", a.username as "Username"
            from account_sessions s
            join accounts a on a.id = s.account_id
            where s.token_hash = @TokenHash
              and s.revoked_at is null
              and s.expires_at > now();
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var account = await connection.QuerySingleOrDefaultAsync<AuthenticatedAccount>(
            new CommandDefinition(sql, new { TokenHash = tokenHash }, cancellationToken: cancellationToken));

        return account is null
            ? ServiceResult<AuthenticatedAccount>.Unauthorized("invalid_session_token", "Session token is invalid or expired.")
            : ServiceResult<AuthenticatedAccount>.Ok(account);
    }

    private static string? ReadBearerToken(HttpRequest request)
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
