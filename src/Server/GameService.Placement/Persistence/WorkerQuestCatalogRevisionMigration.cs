namespace Mmorpg.GameService.Placement.Persistence;

internal static class WorkerQuestCatalogRevisionMigration
{
    public const string Id = "202611051200_worker_quest_catalog_revision";

    // A worker reports the quest catalog it counts quest progress with. Placement only
    // places characters on a worker whose quest catalog matches the mirrored one.
    public const string Sql = """
        alter table simulation_workers
            add column quest_catalog_revision text not null default '';
        """;
}
