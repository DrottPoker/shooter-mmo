namespace Mmorpg.GameService.Items.Persistence;

internal static class PermanentInventoryExpansionMigration
{
    public const string Id = "202611021300_expand_permanent_inventory";

    // The permanent inventory grows from 20 to 24 slots, four full rows of six in the
    // inventory window. Every character keeps its items where they are and gains four empty
    // general slots, and the character settings move to the new count, so the catalog
    // seeder accepts the catalog that names it. Only the old count changes: settings or
    // containers with any other count keep it, and the seeder still refuses a mismatch.
    public const string Sql = """
        with expanded as (
            update item_containers container
            set slot_capacity = 24,
                revision = container.revision + 1,
                updated_at = now()
            where container.container_type = 'permanent_inventory'
              and container.slot_capacity = 20
            returning container.id
        ),
        added as (
            insert into item_container_slots (container_id, slot_index, slot_kind)
            select expanded.id, slot_index, 'general'
            from expanded
            cross join generate_series(20, 23) as slot_index
            returning container_id
        )
        update character_item_states state
        set revision = state.revision + 1,
            updated_at = now()
        from expanded
        where state.permanent_inventory_container_id = expanded.id;

        update item_system_settings
        set permanent_inventory_slot_count = 24
        where id = 'character_default'
          and permanent_inventory_slot_count = 20;
        """;
}
