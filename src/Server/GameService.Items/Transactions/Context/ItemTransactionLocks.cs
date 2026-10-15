namespace Mmorpg.GameService.Items.Transactions;

// The lock facet of an item transaction. Handlers lock the advisory key first, then
// character states, then the corpse row, then the mutation scope.
internal sealed class ItemTransactionLocks(
    ItemTransactionSession session,
    ItemActorAuthority authority,
    CharacterItemStateLocks characterStates,
    ItemMutationScopeLocks mutationScope)
{
    public Task<IReadOnlyDictionary<Guid, LockedCharacterState>> LockCharacterStatesAsync(
        IEnumerable<CharacterLockRequest> requests,
        CancellationToken cancellationToken)
    {
        return characterStates.LockAsync(session, authority, requests, cancellationToken);
    }

    public Task<LockedCharacterState> LockCharacterStateForIdempotentEventAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        return characterStates.LockForEventAsync(session, authority, characterId, cancellationToken);
    }

    public Task LockMutationScopeAsync(
        IEnumerable<Guid> itemIds,
        IEnumerable<Guid> containerIds,
        CancellationToken cancellationToken)
    {
        return mutationScope.LockMutationScopeAsync(itemIds, containerIds, cancellationToken);
    }
}
