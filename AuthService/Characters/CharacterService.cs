using AuthService.Http;
using Dapper;
using Npgsql;

namespace AuthService.Characters;

public sealed class CharacterService(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    public async Task<ServiceResult<IReadOnlyCollection<CharacterResponse>>> ListAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id as "Id", name as "Name", currency as "Currency", created_at as "CreatedAt"
            from characters
            where account_id = @AccountId and deleted_at is null
            order by created_at asc;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var characters = await connection.QueryAsync<CharacterResponse>(
            new CommandDefinition(sql, new { AccountId = accountId }, cancellationToken: cancellationToken));

        return ServiceResult<IReadOnlyCollection<CharacterResponse>>.Ok(characters.ToArray());
    }

    public async Task<ServiceResult<CharacterResponse>> CreateAsync(
        Guid accountId,
        CreateCharacterRequest request,
        CancellationToken cancellationToken)
    {
        var nameError = CharacterValidation.ValidateName(request.Name);
        if (nameError is not null)
        {
            return ServiceResult<CharacterResponse>.BadRequest("invalid_character_name", nameError);
        }

        var maxCharacters = configuration.GetValue("Game:MaxCharactersPerAccount", 5);
        var name = request.Name!.Trim();
        var normalizedName = name.ToUpperInvariant();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string lockAccountSql = """
                select id
                from accounts
                where id = @AccountId
                for update;
                """;

            var lockedAccountId = await connection.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    lockAccountSql,
                    new { AccountId = accountId },
                    transaction,
                    cancellationToken: cancellationToken));

            if (lockedAccountId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ServiceResult<CharacterResponse>.NotFound("account_not_found", "Account was not found.");
            }

            const string countSql = """
                select count(*)
                from characters
                where account_id = @AccountId and deleted_at is null;
                """;

            var characterCount = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    countSql,
                    new { AccountId = accountId },
                    transaction,
                    cancellationToken: cancellationToken));

            if (characterCount >= maxCharacters)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ServiceResult<CharacterResponse>.Conflict(
                    "character_limit_reached",
                    $"Account already has the maximum of {maxCharacters} characters.");
            }

            const string insertSql = """
                insert into characters (id, account_id, name, normalized_name)
                values (@Id, @AccountId, @Name, @NormalizedName)
                returning id as "Id", name as "Name", currency as "Currency", created_at as "CreatedAt";
                """;

            var character = await connection.QuerySingleAsync<CharacterResponse>(
                new CommandDefinition(
                    insertSql,
                    new
                    {
                        Id = Guid.NewGuid(),
                        AccountId = accountId,
                        Name = name,
                        NormalizedName = normalizedName
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<CharacterResponse>.Ok(character);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<CharacterResponse>.Conflict("character_name_taken", "Character name is already taken.");
        }
    }
}

