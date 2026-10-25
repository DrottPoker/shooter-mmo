using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.WorldData.Characters;
using Npgsql;

namespace Mmorpg.GameService.Characters;

internal sealed class CharacterService(
    NpgsqlDataSource dataSource,
    CharactersModuleOptions options,
    IAccountLocks accountLocks,
    ICharacterLifecycleHandler characterLifecycleHandler,
    CharacterAppearances appearances)
{
    public async Task<ServiceResult<IReadOnlyCollection<CharacterResponse>>> ListAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select character.id as "Id", character.name as "Name", character.currency as "Currency",
                   character.created_at as "CreatedAt", appearance.body_model as "BodyModel"
            from characters character
            join character_appearances appearance on appearance.character_id = character.id
            where character.account_id = @AccountId and character.deleted_at is null
            order by character.created_at asc;
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

        var (bodyModel, choices, appearanceError) = ReadAppearance(request.Appearance);
        if (appearanceError is not null)
        {
            return ServiceResult<CharacterResponse>.BadRequest(CharacterAppearances.InvalidAppearanceCode, appearanceError);
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
                returning id as "Id", name as "Name", currency as "Currency", created_at as "CreatedAt",
                          @BodyModel as "BodyModel";
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
                        NormalizedName = normalizedName,
                        BodyModel = bodyModel
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            await appearances.InsertAsync(scope, characterId, bodyModel, choices, cancellationToken);

            await characterLifecycleHandler.OnCharacterCreatedAsync(
                scope,
                new CharacterCreated(characterId, accountId),
                cancellationToken);

            // The lifecycle handler may grant the starting balance.
            character = character with
            {
                Currency = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "select currency from characters where id = @Id;",
                    new { Id = characterId },
                    transaction,
                    cancellationToken: cancellationToken))
            };

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

    // What character creation offers, from the appearance content.
    public CharacterAppearanceCatalogResponse AppearanceCatalog()
    {
        var catalog = appearances.Catalog;
        return new CharacterAppearanceCatalogResponse(
            catalog.Kinds.Select(kind => new CharacterAppearanceKindResponse(kind.Id, kind.Required, kind.ChangesInGame)).ToArray(),
            catalog.BodyModels.Select(bodyModel => new CharacterBodyModelResponse(
                bodyModel,
                catalog.OffersFor(bodyModel)
                    .Select(offer => new CharacterAppearanceOfferResponse(offer.Kind, offer.Slot, offer.Options.ToArray()))
                    .ToArray())).ToArray());
    }

    // The requested body model and look, checked as character creation allows them: the default
    // look of the default body model when none is requested, and of the requested body model when
    // it names no choices.
    private (string BodyModel, IReadOnlyList<CharacterAppearanceChoice> Choices, string? Error) ReadAppearance(
        CharacterAppearanceRequest? request)
    {
        var catalog = appearances.Catalog;
        var bodyModel = string.IsNullOrWhiteSpace(request?.BodyModel) ? catalog.DefaultBodyModel : request!.BodyModel!;
        if (request?.Choices is null)
        {
            return catalog.HasBodyModel(bodyModel)
                ? (bodyModel, catalog.DefaultChoices(bodyModel), null)
                : (bodyModel, [], $"The body model '{bodyModel}' does not exist.");
        }

        var choices = request.Choices
            .Select(choice => new CharacterAppearanceChoice(choice?.Kind, choice?.Slot, choice?.Option))
            .ToArray();
        return (bodyModel, choices, catalog.CreationError(bodyModel, choices));
    }
}
