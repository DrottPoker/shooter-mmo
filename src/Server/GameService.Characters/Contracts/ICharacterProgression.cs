using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters.Contracts;

// Character level for other modules, always in the caller's transaction. The highest level
// comes from the progression rules. Experience is not granted yet, so only development tools
// change the level.
public interface ICharacterProgression
{
    // The highest character level, maximumCharacterLevel in the progression rules. Content
    // that requires a higher level can never be used, so the modules refuse to start with it.
    int MaximumLevel { get; }

    // Locks the active character FOR NO KEY UPDATE and sets its level. Returns false when
    // the character does not exist or is deleted.
    Task<bool> SetLevelAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        int level,
        CancellationToken cancellationToken);
}
