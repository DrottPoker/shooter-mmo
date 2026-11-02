namespace Mmorpg.GameService.Items.Persistence;

internal static class PermanentInventoryReductionMigration
{
    public const string Id = "202611021700_reduce_permanent_inventory";

    // The permanent inventory shrinks from 24 back to 20 slots, four full rows of five in the
    // inventory window. An item in one of the four removed slots moves to the lowest free
    // slot that stays, and when none is free into Recovery Storage, as one delivery per
    // character, so no item is lost. The startup carry recompute drops the weight of what
    // moved to Recovery Storage. The character settings move to the new count, so the
    // catalog seeder accepts the catalog that names it. Only the old count changes: settings
    // or containers with any other count keep it, and the seeder still refuses a mismatch.
    public const string Sql = """
        do $$
        declare
            v_inventory record;
            v_item record;
            v_free_slot_index integer;
            v_delivery_id uuid;
            v_next_slot_index integer;
            v_item_order integer;
        begin
            for v_inventory in
                select
                    container.id as container_id,
                    state.character_id,
                    state.recovery_storage_container_id
                from item_containers container
                join character_item_states state
                  on state.permanent_inventory_container_id = container.id
                where container.container_type = 'permanent_inventory'
                  and container.slot_capacity = 24
                order by state.character_id
            loop
                v_delivery_id := null;
                v_item_order := 0;
                for v_item in
                    select id
                    from item_instances
                    where container_id = v_inventory.container_id
                      and container_slot_index >= 20
                    order by container_slot_index
                loop
                    select slot.slot_index
                    into v_free_slot_index
                    from item_container_slots slot
                    where slot.container_id = v_inventory.container_id
                      and slot.slot_index < 20
                      and not exists (
                          select 1
                          from item_instances item
                          where item.container_id = v_inventory.container_id
                            and item.container_slot_index = slot.slot_index)
                    order by slot.slot_index
                    limit 1;

                    if v_free_slot_index is not null then
                        update item_instances
                        set container_slot_index = v_free_slot_index,
                            revision = revision + 1,
                            updated_at = now()
                        where id = v_item.id;
                        continue;
                    end if;

                    if v_delivery_id is null then
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
                            v_inventory.character_id,
                            v_inventory.recovery_storage_container_id,
                            'permanent_inventory_reduction',
                            '202611021700_reduce_permanent_inventory',
                            now());

                        select coalesce(max(slot_index) + 1, 0)
                        into v_next_slot_index
                        from item_container_slots
                        where container_id = v_inventory.recovery_storage_container_id;
                    end if;

                    insert into item_container_slots (container_id, slot_index, slot_kind)
                    values (
                        v_inventory.recovery_storage_container_id,
                        v_next_slot_index + v_item_order,
                        'general');

                    update item_instances
                    set container_id = v_inventory.recovery_storage_container_id,
                        container_slot_index = v_next_slot_index + v_item_order,
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

                delete from item_container_slots
                where container_id = v_inventory.container_id
                  and slot_index >= 20;

                update item_containers
                set slot_capacity = 20,
                    revision = revision + 1,
                    updated_at = now()
                where id = v_inventory.container_id;

                if v_delivery_id is not null then
                    update item_containers
                    set revision = revision + 1,
                        updated_at = now()
                    where id = v_inventory.recovery_storage_container_id;
                end if;

                update character_item_states
                set revision = revision + 1,
                    updated_at = now()
                where character_id = v_inventory.character_id;
            end loop;

            if exists (
                select 1
                from item_instances item
                join item_containers container on container.id = item.container_id
                where container.container_type = 'permanent_inventory'
                  and item.container_slot_index >= container.slot_capacity) then
                raise exception 'Items remain beyond the permanent inventory capacity.';
            end if;
        end;
        $$;

        update item_system_settings
        set permanent_inventory_slot_count = 20
        where id = 'character_default'
          and permanent_inventory_slot_count = 24;
        """;
}
