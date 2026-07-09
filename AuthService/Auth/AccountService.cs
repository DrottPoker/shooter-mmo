using AuthService.Http;
using BCrypt.Net;
using Dapper;
using Npgsql;

namespace AuthService.Auth;

public sealed class AccountService(NpgsqlDataSource dataSource, SessionService sessionService)
{
    public async Task<ServiceResult<AuthResponse>> RegisterAsync(
        RegisterAccountRequest request,
        CancellationToken cancellationToken)
    {
        var emailError = AccountValidation.ValidateEmail(request.Email);
        if (emailError is not null)
        {
            return ServiceResult<AuthResponse>.BadRequest("invalid_email", emailError);
        }

        var usernameError = AccountValidation.ValidateUsername(request.Username);
        if (usernameError is not null)
        {
            return ServiceResult<AuthResponse>.BadRequest("invalid_username", usernameError);
        }

        var passwordError = AccountValidation.ValidatePassword(request.Password);
        if (passwordError is not null)
        {
            return ServiceResult<AuthResponse>.BadRequest("invalid_password", passwordError);
        }

        var email = request.Email!.Trim();
        var username = request.Username!.Trim();
        var normalizedEmail = email.ToUpperInvariant();
        var normalizedUsername = username.ToUpperInvariant();
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password!, workFactor: 12);
        var accountId = Guid.NewGuid();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var existingAccount = await FindExistingAccountAsync(
            connection,
            normalizedEmail,
            normalizedUsername,
            cancellationToken);

        if (existingAccount == "email")
        {
            return ServiceResult<AuthResponse>.Conflict("email_already_used", "Email is already registered.");
        }

        if (existingAccount == "username")
        {
            return ServiceResult<AuthResponse>.Conflict("username_already_used", "Username is already taken.");
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string insertAccountSql = """
                insert into accounts (id, email, normalized_email, username, normalized_username, password_hash)
                values (@Id, @Email, @NormalizedEmail, @Username, @NormalizedUsername, @PasswordHash);
                """;

            await connection.ExecuteAsync(new CommandDefinition(
                insertAccountSql,
                new
                {
                    Id = accountId,
                    Email = email,
                    NormalizedEmail = normalizedEmail,
                    Username = username,
                    NormalizedUsername = normalizedUsername,
                    PasswordHash = passwordHash
                },
                transaction,
                cancellationToken: cancellationToken));

            var session = await sessionService.CreateSessionAsync(
                connection,
                transaction,
                accountId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return ServiceResult<AuthResponse>.Ok(new AuthResponse(
                accountId,
                username,
                session.Token,
                session.ExpiresAt));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<AuthResponse>.Conflict("account_already_exists", "Email or username is already used.");
        }
    }

    public async Task<ServiceResult<AuthResponse>> LoginAsync(
        LoginAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrWhiteSpace(request.Password))
        {
            return ServiceResult<AuthResponse>.BadRequest("invalid_login", "Login and password are required.");
        }

        var normalizedLogin = request.Login.Trim().ToUpperInvariant();

        const string sql = """
            select id as "Id", username as "Username", password_hash as "PasswordHash"
            from accounts
            where normalized_email = @Login or normalized_username = @Login;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var account = await connection.QuerySingleOrDefaultAsync<AccountLoginRow>(
            new CommandDefinition(sql, new { Login = normalizedLogin }, cancellationToken: cancellationToken));

        if (account is null || !BCrypt.Net.BCrypt.Verify(request.Password, account.PasswordHash))
        {
            return ServiceResult<AuthResponse>.Unauthorized("invalid_credentials", "Login or password is incorrect.");
        }

        var session = await sessionService.CreateSessionAsync(
            connection,
            null,
            account.Id,
            cancellationToken);

        return ServiceResult<AuthResponse>.Ok(new AuthResponse(
            account.Id,
            account.Username,
            session.Token,
            session.ExpiresAt));
    }

    public async Task<ServiceResult<AccountProfileResponse>> GetProfileAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id as "AccountId", email as "Email", username as "Username", created_at as "CreatedAt"
            from accounts
            where id = @AccountId;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var profile = await connection.QuerySingleOrDefaultAsync<AccountProfileResponse>(
            new CommandDefinition(sql, new { AccountId = accountId }, cancellationToken: cancellationToken));

        return profile is null
            ? ServiceResult<AccountProfileResponse>.NotFound("account_not_found", "Account was not found.")
            : ServiceResult<AccountProfileResponse>.Ok(profile);
    }

    private static async Task<string?> FindExistingAccountAsync(
        NpgsqlConnection connection,
        string normalizedEmail,
        string normalizedUsername,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                case
                    when normalized_email = @NormalizedEmail then 'email'
                    when normalized_username = @NormalizedUsername then 'username'
                end
            from accounts
            where normalized_email = @NormalizedEmail or normalized_username = @NormalizedUsername
            limit 1;
            """;

        return await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                sql,
                new { NormalizedEmail = normalizedEmail, NormalizedUsername = normalizedUsername },
                cancellationToken: cancellationToken));
    }

    private sealed record AccountLoginRow(Guid Id, string Username, string PasswordHash);
}

