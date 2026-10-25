using Mmorpg.GameService.Platform.Database;
using Mmorpg.WorldData.Characters;

namespace Mmorpg.GameService.Characters.Contracts;

// How a character looks: its body model, the choices of its look, and whether it has lowered
// its headwear, such as a hood pushed back. See docs/EQUIPMENT_VISUALS_DESIGN.md, Items.
public sealed record CharacterAppearanceState(
    string BodyModel,
    IReadOnlyList<CharacterAppearanceChoice> Choices,
    bool HeadwearLowered);

// Why a change of a look was refused, as a stable code with a message, or null when it was stored.
public sealed record CharacterAppearanceChange(CharacterAppearanceState? Appearance, string? ErrorCode, string? ErrorMessage)
{
    public static CharacterAppearanceChange Stored(CharacterAppearanceState appearance) => new(appearance, null, null);

    public static CharacterAppearanceChange Refused(string code, string message) => new(null, code, message);
}

// Character looks for other modules, always in the caller's transaction. Characters owns the
// looks and checks every one it stores against the appearance content.
public interface ICharacterAppearances
{
    // The appearance content every stored look follows.
    CharacterAppearanceCatalog Catalog { get; }

    // The look of an active character, or null when it does not exist or is deleted.
    Task<CharacterAppearanceState?> ReadAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken);

    // The looks of the characters that exist, deleted ones too, such as the source characters of
    // corpses.
    Task<IReadOnlyDictionary<Guid, CharacterAppearanceState>> ReadManyAsync(
        PostgresTransactionScope scope,
        IReadOnlyCollection<Guid> characterIds,
        CancellationToken cancellationToken);

    // Locks the active character FOR NO KEY UPDATE and lowers or raises its headwear.
    Task<CharacterAppearanceChange> SetHeadwearLoweredAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        bool lowered,
        CancellationToken cancellationToken);

    // Locks the active character FOR NO KEY UPDATE and stores another body model and look, which
    // only development tools do: a body model and the creation-only kinds never change in the game.
    Task<CharacterAppearanceChange> SetDevelopmentLookAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string bodyModel,
        IReadOnlyList<CharacterAppearanceChoice> choices,
        CancellationToken cancellationToken);
}
