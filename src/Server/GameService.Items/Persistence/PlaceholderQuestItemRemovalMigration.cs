namespace Mmorpg.GameService.Items.Persistence;

internal static class PlaceholderQuestItemRemovalMigration
{
    public const string Id = "202611051300_remove_placeholder_quest_items";

    // Before quest content existed, every quest NPC offered one configured quest that granted a
    // Sealed Letter with the lineage quest.local.sealed_letter. Quest content replaced it, so no
    // quest can take those letters back, and a protected quest item can never be destroyed by
    // its owner. They leave every inventory, Bag, Bank, and Recovery Storage, each with a
    // destruction record of one system operation, and a Recovery delivery that loses its last
    // item goes with it. The startup carry recompute drops their weight.
    public const string Sql = """
        do $$
        declare
            v_operation_id constant uuid := '8f4b6c2e-1d7a-4e93-b5c8-3a9f0d2e6b71';
            v_item record;
        begin
            if not exists (
                select 1
                from item_instance_policies
                where source_kind = 'quest_grant'
                  and source_id = 'quest.local.sealed_letter') then
                return;
            end if;

            insert into item_operations (
                operation_id,
                operation_kind,
                request_hash,
                status,
                request_payload,
                result_payload,
                completed_at)
            values (
                v_operation_id,
                'remove_placeholder_quest_items',
                encode(sha256(convert_to('202611051300_remove_placeholder_quest_items', 'UTF8')), 'hex'),
                'committed',
                '{}'::jsonb,
                '{}'::jsonb,
                now());

            for v_item in
                select distinct
                    item.id,
                    item.definition_id,
                    item.quantity,
                    item.container_id,
                    item.container_slot_index,
                    container.container_type,
                    coalesce(
                        item.equipped_character_id,
                        container.owner_character_id,
                        source_bag.equipped_character_id) as owner_character_id
                from item_instance_policies policy
                join item_instances item
                  on item.id = policy.item_instance_id
                left join item_containers container
                  on container.id = item.container_id
                left join item_instances source_bag
                  on source_bag.id = container.bound_bag_item_instance_id
                where policy.source_kind = 'quest_grant'
                  and policy.source_id = 'quest.local.sealed_letter'
                order by item.id
            loop
                insert into item_destructions (
                    item_instance_id,
                    definition_id,
                    source_operation_id,
                    quantity,
                    reason)
                values (
                    v_item.id,
                    v_item.definition_id,
                    v_operation_id,
                    v_item.quantity,
                    'quest_retired');

                delete from item_instances
                where id = v_item.id;

                -- Recovery items own their slot row, and an empty delivery can never be claimed.
                if v_item.container_type = 'recovery_storage' then
                    delete from item_container_slots
                    where container_id = v_item.container_id
                      and slot_index = v_item.container_slot_index;

                    delete from recovery_deliveries delivery
                    where delivery.recovery_storage_container_id = v_item.container_id
                      and delivery.claimed_at is null
                      and not exists (
                          select 1
                          from recovery_delivery_items link
                          where link.recovery_delivery_id = delivery.id);
                end if;

                if v_item.container_id is not null then
                    update item_containers
                    set revision = revision + 1,
                        updated_at = now()
                    where id = v_item.container_id;
                end if;

                if v_item.owner_character_id is not null then
                    update character_item_states
                    set revision = revision + 1,
                        updated_at = now()
                    where character_id = v_item.owner_character_id;
                end if;
            end loop;
        end;
        $$;
        """;
}
