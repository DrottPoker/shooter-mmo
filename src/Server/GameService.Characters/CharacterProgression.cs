using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters;

internal sealed class CharacterProgression : ICharacterProgression
{
    public async Task<bool> SetLevelAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        int level,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);

        var locked = await scope.Connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
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
        if (locked is null)
        {
            return false;
        }

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            "update characters set level = @Level where id = @CharacterId;",
            new { CharacterId = characterId, Level = level },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return true;
    }
}
