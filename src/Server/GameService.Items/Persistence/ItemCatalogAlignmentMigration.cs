namespace Mmorpg.GameService.Items.Persistence;

internal static class ItemCatalogAlignmentMigration
{
    public const string Id = "202610171200_item_catalog_alignment";

    // Equipment slots carry a display order, a stack quantity, and a retired state from the
    // catalog. A corpse equipment slot names its equipment slot instead of reusing the slot
    // order as its index, so the display order can change and new slots never collide.
    // Corpse equipment and Bag sections may have no slots: a Mob corpse carries only general
    // loot, and a player without a Bag leaves an empty Bag section.
    public const string Sql = """
        alter table equipment_slots rename column sort_order to display_order;
        alter table equipment_slots drop constraint equipment_slots_sort_order_key;
        alter table equipment_slots
            rename constraint ck_equipment_slots_sort_order to ck_equipment_slots_display_order;
        alter table equipment_slots
            add column maximum_quantity integer not null default 1,
            add column is_active boolean not null default true,
            add constraint ck_equipment_slots_maximum_quantity
                check (maximum_quantity between 1 and 1000000);

        -- An equipped item keeps these slots empty, such as a two-handed weapon the off hand.
        create table item_definition_blocked_equipment_slots (
            definition_id text not null references item_definitions(id) on delete cascade,
            equipment_slot_id text not null references equipment_slots(id) on delete restrict,
            constraint pk_item_definition_blocked_equipment_slots
                primary key (definition_id, equipment_slot_id)
        );

        alter table item_container_slots
            add column equipment_slot_id text null
                references equipment_slots(id) on delete restrict;

        update item_container_slots slot
        set equipment_slot_id = equipment.id
        from item_containers container, equipment_slots equipment
        where container.id = slot.container_id
          and container.container_type = 'corpse_equipment'
          and equipment.display_order = slot.slot_index;

        create unique index ux_item_container_slots_equipment_slot
            on item_container_slots(container_id, equipment_slot_id)
            where equipment_slot_id is not null;

        alter table item_containers drop constraint ck_item_containers_capacity;
        alter table item_containers
            add constraint ck_item_containers_capacity
                check (
                    (container_type = 'recovery_storage' and slot_capacity is null)
                    or
                    (container_type in ('corpse_equipment', 'corpse_bag_contents')
                     and slot_capacity between 0 and 65535)
                    or
                    (container_type not in (
                        'recovery_storage',
                        'corpse_equipment',
                        'corpse_bag_contents')
                     and slot_capacity between 1 and 65535));
        """;
}
