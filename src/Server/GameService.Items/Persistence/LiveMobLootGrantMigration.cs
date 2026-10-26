namespace Mmorpg.GameService.Items.Persistence;

internal static class LiveMobLootGrantMigration
{
    public const string Id = "202610261800_live_mob_loot_grants";

    // Every committed live Mob loot grant, by the grant id the worker derived for the loot
    // entry. A grant that one Loot all already committed is never committed again, by another
    // Loot all or by a single loot, even when the worker retries after an uncertain outcome.
    public const string Sql = """
        create table live_mob_loot_grants (
            grant_id uuid primary key,
            operation_id uuid not null,
            character_id uuid not null,
            source_corpse_id uuid not null,
            granted_at timestamptz not null default now()
        );
        """;
}
