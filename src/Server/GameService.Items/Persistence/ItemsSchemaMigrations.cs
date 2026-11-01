using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Items.Persistence;

// Migrations owned by Items. The host merges them with every other module's by id.
internal static class ItemsSchemaMigrations
{
    public static IReadOnlyList<DatabaseMigration> All { get; } =
    [
        new(ItemPersistenceFoundationMigration.Id, ItemPersistenceFoundationMigration.Sql),
        new(PlayerCorpsePersistenceMigration.Id, PlayerCorpsePersistenceMigration.Sql),
        new(PlayerDeathCarryOverflowMigration.Id, PlayerDeathCarryOverflowMigration.Sql),
        new(ItemOperationsHardeningMigration.Id, ItemOperationsHardeningMigration.Sql),
        new(
            ReleaseRetryableOperationClaimsMigration.Id,
            ReleaseRetryableOperationClaimsMigration.Sql),
        new(EquippedItemLookupIndexMigration.Id, EquippedItemLookupIndexMigration.Sql),
        new(ItemCatalogAlignmentMigration.Id, ItemCatalogAlignmentMigration.Sql),
        new(ItemLevelRequirementMigration.Id, ItemLevelRequirementMigration.Sql),
        new(DeathEventKillerMigration.Id, DeathEventKillerMigration.Sql),
        new(ItemEconomyMigration.Id, ItemEconomyMigration.Sql),
        new(LiveMobLootGrantMigration.Id, LiveMobLootGrantMigration.Sql),
        new(SecureContainerRemovalMigration.Id, SecureContainerRemovalMigration.Sql)
    ];
}
