using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement.Persistence;

// Migrations owned by Placement. The host merges them with every other module's by id.
internal static class PlacementSchemaMigrations
{
    public static IReadOnlyList<DatabaseMigration> All { get; } =
    [
        new(DevelopmentWorldIdentityMigration.Id, DevelopmentWorldIdentityMigration.Sql),
        new(WorkerSelectedWorldBindingMigration.Id, WorkerSelectedWorldBindingMigration.Sql),
        new(WorkerItemCatalogRevisionMigration.Id, WorkerItemCatalogRevisionMigration.Sql),
        new(WorkerQuestCatalogRevisionMigration.Id, WorkerQuestCatalogRevisionMigration.Sql),
        new(SimulationSessionAccountEndMigration.Id, SimulationSessionAccountEndMigration.Sql)
    ];
}
