using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters.Contracts;

// Character level for other modules, always in the caller's transaction. Experience and
// levelling do not exist yet, so only development tools change the level.
public interface ICharacterProgression
{
    // Locks the active character FOR NO KEY UPDATE and sets its level. Returns false when
    // the character does not exist or is deleted.
    Task<bool> SetLevelAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        int level,
        CancellationToken cancellationToken);
}
