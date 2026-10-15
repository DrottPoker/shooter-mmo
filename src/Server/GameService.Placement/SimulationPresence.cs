using Dapper;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement;

public sealed class SimulationPresence : ISimulationPresence
{
    public Task<Guid?> FindActiveCharacterAsync(
        PostgresTransactionScope scope,
        IReadOnlyCollection<Guid> characterIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(characterIds);

        return scope.Connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                select character_id
                from character_simulation_sessions
                where character_id = any(@CharacterIds)
                  and released_at is null
                  and expires_at > now()
                order by character_id
                limit 1;
                """,
                new { CharacterIds = characterIds.ToArray() },
                scope.Transaction,
                cancellationToken: cancellationToken));
    }
}
