using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters.Contracts;

// Row locks on characters for other modules, always in the caller's transaction.
// Placement locks FOR UPDATE to change who plays a character. Item mutations lock FOR
// NO KEY UPDATE, which conflicts with placement but not with each other's key reads.
public interface ICharacterLocks
{
    // Locks an active character owned by the account FOR UPDATE. Returns false when no
    // such character exists.
    Task<bool> LockOwnedActiveForUpdateAsync(
        PostgresTransactionScope scope,
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken);

    // Locks the character FOR UPDATE whether or not it is deleted.
    Task LockForUpdateAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken);

    // Locks an active character FOR NO KEY UPDATE and returns its account id, or null
    // when the character does not exist or is deleted.
    Task<Guid?> LockActiveForItemMutationAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken);

    // Locks the active characters among the ids FOR NO KEY UPDATE in ascending id order
    // and returns each locked character's account id.
    Task<IReadOnlyDictionary<Guid, Guid>> LockActiveForItemMutationAsync(
        PostgresTransactionScope scope,
        IReadOnlyCollection<Guid> characterIds,
        CancellationToken cancellationToken);
}
