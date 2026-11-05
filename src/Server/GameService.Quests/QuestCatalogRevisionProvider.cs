using Dapper;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Quests.Contracts;

namespace Mmorpg.GameService.Quests;

// The mirrored revision of the quest catalog that GameService runs, for Placement's check
// that a worker counts quests with the same catalog.
internal sealed class QuestCatalogRevisionProvider(IQuestDefinitions questDefinitions)
    : IQuestCatalogRevisionProvider
{
    public Task<string?> GetCurrentRevisionAsync(
        PostgresTransactionScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "select revision from quest_catalogs where catalog_id = @CatalogId;",
            new { questDefinitions.CatalogId },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }
}
