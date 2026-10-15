using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Npgsql;

namespace Mmorpg.GameService.Characters;

public sealed class CharacterService(
    NpgsqlDataSource dataSource,
    CharactersModuleOptions options,
    IAccountLocks accountLocks,
    ICharacterLifecycleHandler characterLifecycleHandler)
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

        var maxCharacters = options.MaxCharactersPerAccount;
        var name = request.Name!.Trim();
        var normalizedName = name.ToUpperInvariant();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var scope = new PostgresTransactionScope(connection, transaction);
            if (!await accountLocks.LockForUpdateAsync(scope, accountId, cancellationToken))
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

            var characterId = Guid.NewGuid();
            var character = await connection.QuerySingleAsync<CharacterResponse>(
                new CommandDefinition(
                    insertSql,
                    new
                    {
                        Id = characterId,
                        AccountId = accountId,
                        Name = name,
                        NormalizedName = normalizedName
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            await characterLifecycleHandler.OnCharacterCreatedAsync(
                scope,
                new CharacterCreated(characterId, accountId),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<CharacterResponse>.Ok(character);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation
            && exception.ConstraintName == "characters_normalized_name_key")
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<CharacterResponse>.Conflict("character_name_taken", "Character name is already taken.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
