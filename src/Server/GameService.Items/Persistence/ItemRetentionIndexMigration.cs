namespace Mmorpg.GameService.Items.Persistence;

internal static class ItemRetentionIndexMigration
{
    public const string Id = "202611141300_item_retention_indexes";

    // The retention tasks delete the oldest due rows first, so each table needs an index in the
    // order of its time column. Operations and closed corpses have theirs already. The migration
    // builds the indexes inside its transaction, which blocks writes to each table while it builds.
    // That is fine at today's sizes; a large live table needs a migration from
    // DatabaseMigration.CreateIndexConcurrently, which the runner runs outside a transaction.
    public const string Sql = """
        create index ix_item_destructions_retention
            on item_destructions(destroyed_at, item_instance_id);

        create index ix_recovery_deliveries_claimed_retention
            on recovery_deliveries(claimed_at, id)
            where claimed_at is not null;

        create index ix_live_mob_loot_grants_retention
            on live_mob_loot_grants(granted_at, grant_id);
        """;
}
