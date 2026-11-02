namespace Mmorpg.GameService.Items.Persistence;

internal static class CloakEquipmentRemovalMigration
{
    public const string Id = "202611021200_remove_cloak_equipment";

    // Cloaks become achievement rewards that are worn from their own window later, so the
    // wool cloaks can no longer be equipped and the Back slot is hidden. Every item equipped
    // in the Back slot moves into Recovery Storage as one delivery per character, so no item
    // is lost and the slot stays empty. Removing the Back slot from the cloak definitions
    // changes their structural fingerprints, so the stored fingerprints as they were before
    // are restamped to their new values. A definition with any other stored fingerprint keeps
    // it, and the catalog seeder still refuses a structural change of a definition with live
    // instances.
    public const string Sql = """
        do $$
        declare
            v_state record;
            v_item record;
            v_delivery_id uuid;
            v_next_slot_index integer;
            v_item_order integer;
        begin
            for v_state in
                select
                    state.character_id,
                    state.recovery_storage_container_id
                from character_item_states state
                where exists (
                    select 1
                    from item_instances item
                    where item.equipped_character_id = state.character_id
                      and item.equipment_slot_id = 'back')
                order by state.character_id
            loop
                v_delivery_id := gen_random_uuid();
                insert into recovery_deliveries (
                    id,
                    character_id,
                    recovery_storage_container_id,
                    source_kind,
                    source_event_id,
                    available_at)
                values (
                    v_delivery_id,
                    v_state.character_id,
                    v_state.recovery_storage_container_id,
                    'cloak_equipment_removal',
                    '202611021200_remove_cloak_equipment',
                    now());

                select coalesce(max(slot_index) + 1, 0)
                into v_next_slot_index
                from item_container_slots
                where container_id = v_state.recovery_storage_container_id;

                v_item_order := 0;
                for v_item in
                    select id
                    from item_instances
                    where equipped_character_id = v_state.character_id
                      and equipment_slot_id = 'back'
                    order by id
                loop
                    insert into item_container_slots (container_id, slot_index, slot_kind)
                    values (
                        v_state.recovery_storage_container_id,
                        v_next_slot_index + v_item_order,
                        'general');

                    update item_instances
                    set container_id = v_state.recovery_storage_container_id,
                        container_slot_index = v_next_slot_index + v_item_order,
                        equipped_character_id = null,
                        equipment_slot_id = null,
                        revision = revision + 1,
                        updated_at = now()
                    where id = v_item.id;

                    insert into recovery_delivery_items (
                        recovery_delivery_id,
                        item_instance_id,
                        item_order)
                    values (v_delivery_id, v_item.id, v_item_order);

                    v_item_order := v_item_order + 1;
                end loop;

                update item_containers
                set revision = revision + 1,
                    updated_at = now()
                where id = v_state.recovery_storage_container_id;

                update character_item_states
                set revision = revision + 1,
                    updated_at = now()
                where character_id = v_state.character_id;
            end loop;

            if exists (
                select 1
                from item_instances
                where equipment_slot_id = 'back') then
                raise exception 'Items remain in the Back slot after the move to Recovery Storage.';
            end if;
        end;
        $$;

        update item_definitions definition
        set structural_fingerprint = restamp.new_fingerprint
        from (values
            ('clothing.blue_wool_cloak', '9c6014810905415494c9b20b3df04aa777e475911f1fdbe0e58c453810b8e641', 'deec133dcdd08ddb4ae43354b1f70b9f69ffd68956fa871750f6ef695cc4ee4f'),
            ('clothing.red_wool_cloak', '01a030b667e0e474918fb341b72b4f98a32fd0365bda81c026b39e6946e3c80e', '62d0ee09b90ab03740d7a61a500df719fb77d0654061cfb51346de12b23bd6ec'),
            ('clothing.wool_cloak', '7cda73642793f84b2d8bb5056a2dee9155163c4c1835dd1b8b0b4f4b97e2e88f', 'be1df35f526ffd50e01ea2d8d20202284b54a1df3904bf5a5a5300feef6e6a72'))
            as restamp(id, old_fingerprint, new_fingerprint)
        where definition.id = restamp.id
          and definition.structural_fingerprint = restamp.old_fingerprint;
        """;
}
