using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.WorldData.Characters;

namespace Mmorpg.GameService.Characters;

internal sealed class CharacterAppearances(CharacterAppearanceCatalog catalog) : ICharacterAppearances
{
    public const string CharacterNotFoundCode = "character_not_found";
    public const string InvalidAppearanceCode = "invalid_character_appearance";

    public CharacterAppearanceCatalog Catalog => catalog;

    public async Task<CharacterAppearanceState?> ReadAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var row = await scope.Connection.QuerySingleOrDefaultAsync<AppearanceRow>(new CommandDefinition(
            """
            select appearance.character_id as "CharacterId",
                   appearance.body_model as "BodyModel",
                   appearance.choices::text as "Choices",
                   appearance.headwear_lowered as "HeadwearLowered"
            from character_appearances appearance
            join characters character on character.id = appearance.character_id
            where appearance.character_id = @CharacterId
              and character.deleted_at is null;
            """,
            new { CharacterId = characterId },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return row?.ToState();
    }

    public async Task<IReadOnlyDictionary<Guid, CharacterAppearanceState>> ReadManyAsync(
        PostgresTransactionScope scope,
        IReadOnlyCollection<Guid> characterIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(characterIds);
        if (characterIds.Count == 0)
        {
            return new Dictionary<Guid, CharacterAppearanceState>();
        }

        var rows = await scope.Connection.QueryAsync<AppearanceRow>(new CommandDefinition(
            """
            select character_id as "CharacterId",
                   body_model as "BodyModel",
                   choices::text as "Choices",
                   headwear_lowered as "HeadwearLowered"
            from character_appearances
            where character_id = any(@CharacterIds);
            """,
            new { CharacterIds = characterIds.Distinct().ToArray() },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return rows.ToDictionary(row => row.CharacterId, row => row.ToState());
    }

    public async Task<CharacterAppearanceChange> SetHeadwearLoweredAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        bool lowered,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!await LockActiveCharacterAsync(scope, characterId, cancellationToken))
        {
            return CharacterAppearanceChange.Refused(CharacterNotFoundCode, "Character was not found.");
        }

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            update character_appearances
            set headwear_lowered = @Lowered, updated_at = now()
            where character_id = @CharacterId;
            """,
            new { CharacterId = characterId, Lowered = lowered },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return await StoredAsync(scope, characterId, cancellationToken);
    }

    public async Task<CharacterAppearanceChange> SetDevelopmentLookAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string bodyModel,
        IReadOnlyList<CharacterAppearanceChoice> choices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var error = catalog.StorageError(bodyModel, choices);
        if (error is not null)
        {
            return CharacterAppearanceChange.Refused(InvalidAppearanceCode, error);
        }

        if (!await LockActiveCharacterAsync(scope, characterId, cancellationToken))
        {
            return CharacterAppearanceChange.Refused(CharacterNotFoundCode, "Character was not found.");
        }

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            update character_appearances
            set body_model = @BodyModel, choices = @Choices::jsonb, updated_at = now()
            where character_id = @CharacterId;
            """,
            new { CharacterId = characterId, BodyModel = bodyModel, Choices = SerializeChoices(choices) },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return await StoredAsync(scope, characterId, cancellationToken);
    }

    // Stores the first look of a new character in its creation transaction.
    public Task InsertAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string bodyModel,
        IReadOnlyList<CharacterAppearanceChoice> choices,
        CancellationToken cancellationToken)
    {
        return scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into character_appearances (character_id, body_model, choices)
            values (@CharacterId, @BodyModel, @Choices::jsonb);
            """,
            new { CharacterId = characterId, BodyModel = bodyModel, Choices = SerializeChoices(choices) },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }

    public static string SerializeChoices(IReadOnlyList<CharacterAppearanceChoice> choices)
    {
        return JsonSerializer.Serialize(choices.Select(choice => new StoredChoice(choice.Kind, choice.Slot, choice.Option)));
    }

    public static IReadOnlyList<CharacterAppearanceChoice> DeserializeChoices(string json)
    {
        return (JsonSerializer.Deserialize<StoredChoice[]>(json) ?? [])
            .Select(choice => new CharacterAppearanceChoice(choice.Kind, choice.Slot, choice.Option))
            .ToArray();
    }

    private async Task<CharacterAppearanceChange> StoredAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(scope, characterId, cancellationToken)
            ?? throw new InvalidOperationException($"Character {characterId} has no look.");
        return CharacterAppearanceChange.Stored(stored);
    }

    private static async Task<bool> LockActiveCharacterAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var locked = await scope.Connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            select id
            from characters
            where id = @CharacterId
              and deleted_at is null
            for no key update;
            """,
            new { CharacterId = characterId },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return locked is not null;
    }

    // The stored form of a choice, in the order and names of the JSONB objects.
    private sealed record StoredChoice(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("option")] string Option);

    private sealed class AppearanceRow
    {
        public Guid CharacterId { get; init; }

        public string BodyModel { get; init; } = string.Empty;

        public string Choices { get; init; } = "[]";

        public bool HeadwearLowered { get; init; }

        public CharacterAppearanceState ToState()
        {
            return new CharacterAppearanceState(BodyModel, DeserializeChoices(Choices), HeadwearLowered);
        }
    }
}
