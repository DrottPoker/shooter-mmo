namespace Mmorpg.GameService.Placement.Persistence;

public static class WorkerSelectedWorldBindingMigration
{
    public const string Id = "202607191200_worker_selected_world_binding";

    public const string Sql = """
        alter table shards
            alter column world_id drop not null;
        """;
}
