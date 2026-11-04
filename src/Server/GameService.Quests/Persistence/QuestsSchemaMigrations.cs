using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Quests.Persistence;

// Migrations owned by Quests. The host merges them with every other module's by id.
internal static class QuestsSchemaMigrations
{
    public static IReadOnlyList<DatabaseMigration> All { get; } =
    [
        new(QuestLogMigration.Id, QuestLogMigration.Sql)
    ];
}
