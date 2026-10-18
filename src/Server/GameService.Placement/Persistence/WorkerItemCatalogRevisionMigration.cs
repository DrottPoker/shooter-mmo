namespace Mmorpg.GameService.Placement.Persistence;

internal static class WorkerItemCatalogRevisionMigration
{
    public const string Id = "202610181230_worker_item_catalog_revision";

    // A worker reports the item catalog it reads combat data and loot rules from. Placement
    // only places characters on a worker whose catalog matches the mirrored one.
    public const string Sql = """
        alter table simulation_workers
            add column item_catalog_revision text not null default '';
        """;
}
