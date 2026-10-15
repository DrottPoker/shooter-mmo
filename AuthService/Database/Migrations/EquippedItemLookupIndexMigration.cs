namespace AuthService.Database.Migrations;

public static class EquippedItemLookupIndexMigration
{
    public const string Id = "202610151000_equipped_item_lookup_index";

    // Carry recompute and death partition look up a character's equipped items on every
    // commit, which scanned the whole item table without this index.
    public const string Sql = """
        create index if not exists ix_item_instances_equipped_character
            on item_instances(equipped_character_id, equipment_slot_id)
            where equipped_character_id is not null;
        """;
}
