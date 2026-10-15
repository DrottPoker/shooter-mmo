using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters;

internal sealed class CharacterLocks : ICharacterLocks
{
    public async Task<bool> LockOwnedActiveForUpdateAsync(
        PostgresTransactionScope scope,
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var lockedCharacterId = await scope.Connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                select id
                from characters
                where id = @CharacterId
                  and account_id = @AccountId
                  and deleted_at is null
                for update;
                """,
                new { AccountId = accountId, CharacterId = characterId },
                scope.Transaction,
                cancellationToken: cancellationToken));

        return lockedCharacterId is not null;
    }

    public async Task LockForUpdateAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await scope.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "select id from characters where id = @CharacterId for update;",
            new { CharacterId = characterId },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<Guid?> LockActiveForItemMutationAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.Connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                select account_id
                from characters
                where id = @CharacterId
                  and deleted_at is null
                for no key update;
                """,
                new { CharacterId = characterId },
                scope.Transaction,
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> LockActiveForItemMutationAsync(
        PostgresTransactionScope scope,
        IReadOnlyCollection<Guid> characterIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(characterIds);

        // Ascending id order, so concurrent multi-character mutations never deadlock.
        var rows = await scope.Connection.QueryAsync<LockedCharacterRow>(
            new CommandDefinition(
                """
                select
                    id as "CharacterId",
                    account_id as "AccountId"
                from characters
                where id = any(@CharacterIds)
                  and deleted_at is null
                order by id
                for no key update;
                """,
                new { CharacterIds = characterIds.ToArray() },
                scope.Transaction,
                cancellationToken: cancellationToken));

        return rows.ToDictionary(row => row.CharacterId, row => row.AccountId);
    }

    private sealed record LockedCharacterRow(Guid CharacterId, Guid AccountId);
}
