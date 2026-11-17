using Mmorpg.GameService.Platform.Audit;

namespace Mmorpg.GameService.Items.Audit;

// The economy audit's checks of what exists now: where every item instance is, its slot, its
// quantity, and its definition, by the custody model of docs/INVENTORY_AND_DEATH_LOOT_DESIGN.md.
internal static class ItemCustodyAuditChecks
{
    private const string ItemCount = "select count(*) from item_instances";

    // Every place that claims a container, and whether the claim is live. A character's item state
    // names its permanent inventory, bank, and Recovery Storage. A corpse section names a container
    // of the section's kind; the Bag section of a corpse may instead be the contents of the Bag
    // that the same corpse holds in its equipment section. A Bag in a character's Bag slot claims
    // its contents. A claim is live only while its container is active, its corpse is open, and
    // the container is of the kind that the claim names.
    private const string ItemPlaces = """
        with container_claim as (
            select
                state.permanent_inventory_container_id as container_id,
                container.container_type = 'permanent_inventory'
                    and container.owner_character_id = state.character_id
                    and container.lifecycle = 'active' as live
            from character_item_states state
            join item_containers container on container.id = state.permanent_inventory_container_id
            union all
            select
                state.bank_container_id,
                container.container_type = 'bank'
                    and container.owner_character_id = state.character_id
                    and container.lifecycle = 'active'
            from character_item_states state
            join item_containers container on container.id = state.bank_container_id
            union all
            select
                state.recovery_storage_container_id,
                container.container_type = 'recovery_storage'
                    and container.owner_character_id = state.character_id
                    and container.lifecycle = 'active'
            from character_item_states state
            join item_containers container on container.id = state.recovery_storage_container_id
            union all
            select
                section.container_id,
                corpse.closed_at is null
                    and container.lifecycle = 'active'
                    and case section.section_kind
                        when 'general_inventory' then container.container_type = 'corpse_inventory'
                        when 'equipment' then container.container_type = 'corpse_equipment'
                        when 'bag' then container.container_type = 'corpse_bag_contents'
                            or (container.container_type = 'bag_contents'
                                and bag.container_id is not distinct from equipment.container_id
                                and bag.container_id is not null)
                        else false
                    end
            from corpse_sections section
            join corpses corpse on corpse.id = section.corpse_id
            join item_containers container on container.id = section.container_id
            left join item_instances bag on bag.id = container.bound_bag_item_instance_id
            left join corpse_sections equipment
              on equipment.corpse_id = section.corpse_id
             and equipment.section_kind = 'equipment'
            union all
            select
                container.id,
                container.lifecycle = 'active'
            from item_containers container
            join item_instances bag on bag.id = container.bound_bag_item_instance_id
            where container.container_type = 'bag_contents'
              and bag.equipment_slot_id = 'bag'
        ),
        container_place as (
            select
                container_id,
                count(*) as claims,
                count(*) filter (where live) as live_claims
            from container_claim
            group by container_id
        ),
        item_place as (
            select
                item.id as item_instance_id,
                item.container_id,
                container.container_type,
                item.equipped_character_id,
                link.recovery_delivery_id,
                coalesce(place.claims, 0) as claims,
                coalesce(place.live_claims, 0) as live_claims,
                state.character_id is not null as equipped_by_item_state,
                coalesce(
                    link.item_instance_id is not null
                        and delivery.claimed_at is null
                        and delivery.recovery_storage_container_id = item.container_id
                        and delivery.character_id = container.owner_character_id,
                    false) as in_its_delivery
            from item_instances item
            left join item_containers container on container.id = item.container_id
            left join container_place place on place.container_id = item.container_id
            left join recovery_delivery_items link on link.item_instance_id = item.id
            left join recovery_deliveries delivery on delivery.id = link.recovery_delivery_id
            left join character_item_states state on state.character_id = item.equipped_character_id
        )
        """;

    // An item in no place is lost for good: nothing can reach it, so nobody can ever use, loot,
    // or claim it. An item in Recovery Storage is in a place only through its delivery, which a
    // claim takes as a whole.
    public static SqlAuditCheck CustodyMissing { get; } = new(
        "item_custody_missing",
        "Every item instance is in a live place: a character's inventory, bank, equipment, or active Bag, an unclaimed Recovery delivery, or a section of an open corpse.",
        AuditSql.Counted(
            ItemCount,
            $"""
            {ItemPlaces}
            select
                item_instance_id,
                case
                    when equipped_character_id is not null then 'equipped_without_item_state'
                    when claims = 0 then 'container_without_owner'
                    when live_claims = 0 then 'container_not_live'
                    else 'recovery_item_without_delivery'
                end as problem,
                container_id,
                container_type,
                equipped_character_id
            from item_place
            where claims <= 1
              and (recovery_delivery_id is null or in_its_delivery)
              and (
                  (equipped_character_id is not null and not equipped_by_item_state)
                  or (container_id is not null and live_claims = 0)
                  or (container_type = 'recovery_storage' and recovery_delivery_id is null))
            """,
            "item_instance_id"));

    // An item in two places could be used, looted, or claimed twice. The schema keeps an item's own
    // row in one container slot or one equipment slot, so a second place comes from a record beside
    // it: a container that two owners claim, or a Recovery delivery that names an item stored
    // elsewhere.
    public static SqlAuditCheck CustodyDuplicated { get; } = new(
        "item_custody_duplicated",
        "No item instance is in two places: no container has two owners, and no Recovery delivery names an item that is not stored in it.",
        AuditSql.Counted(
            ItemCount,
            $"""
            {ItemPlaces}
            select
                item_instance_id,
                case
                    when claims > 1 then 'container_with_two_owners'
                    else 'recovery_delivery_names_item_elsewhere'
                end as problem,
                container_id,
                container_type,
                equipped_character_id,
                recovery_delivery_id
            from item_place
            where claims > 1
               or (recovery_delivery_id is not null and not in_its_delivery)
            """,
            "item_instance_id"));

    public static SqlAuditCheck SlotInvalid { get; } = new(
        "item_slot_invalid",
        "Every item instance's slot exists in its place: a container slot within the container's capacity, or an active equipment slot that its definition fits.",
        AuditSql.Counted(
            ItemCount,
            """
            select *
            from (
                select
                    item.id as item_instance_id,
                    case
                        when item.container_id is not null and slot.container_id is null
                            then 'container_slot_missing'
                        when item.container_slot_index < 0
                            or item.container_slot_index >= container.slot_capacity
                            then 'slot_outside_container'
                        when container.container_type = 'corpse_equipment' and slot.equipment_slot_id is null
                            then 'corpse_equipment_slot_unnamed'
                        when item.equipment_slot_id is not null and not equipment_slot.is_active
                            then 'equipment_slot_retired'
                        when equipment_slot.id is not null and allowed.definition_id is null
                            then 'equipment_slot_not_allowed'
                    end as problem,
                    item.container_id,
                    item.container_slot_index,
                    container.slot_capacity,
                    item.equipped_character_id,
                    equipment_slot.id as equipment_slot_id,
                    item.definition_id
                from item_instances item
                left join item_containers container on container.id = item.container_id
                left join item_container_slots slot
                  on slot.container_id = item.container_id
                 and slot.slot_index = item.container_slot_index
                left join equipment_slots equipment_slot
                  on equipment_slot.id = coalesce(item.equipment_slot_id, slot.equipment_slot_id)
                left join item_definition_equipment_slots allowed
                  on allowed.definition_id = item.definition_id
                 and allowed.equipment_slot_id = equipment_slot.id
            ) checked
            where problem is not null
            """,
            "item_instance_id"));

    // The unique constraints of item_instances hold this already; the check guards against a
    // migration that ever weakens them.
    public static SqlAuditCheck SlotShared { get; } = new(
        "item_slot_shared",
        "No two item instances share a container slot or a character's equipment slot.",
        AuditSql.Counted(
            ItemCount,
            """
            select
                'container_slot' as slot_kind,
                container_id::text as holder_id,
                container_slot_index::text as slot,
                count(*) as item_count,
                string_agg(id::text, ' ' order by id) as item_instance_ids
            from item_instances
            where container_id is not null
            group by container_id, container_slot_index
            having count(*) > 1
            union all
            select
                'equipment_slot',
                equipped_character_id::text,
                equipment_slot_id,
                count(*),
                string_agg(id::text, ' ' order by id)
            from item_instances
            where equipped_character_id is not null
            group by equipped_character_id, equipment_slot_id
            having count(*) > 1
            """,
            "holder_id, slot"));

    // An equipment slot and a corpse equipment slot, which keeps the equipment slot's id, hold at
    // most the slot's maximum quantity, such as 1,000 arrows in the ammunition slot.
    public static SqlAuditCheck QuantityOutOfRange { get; } = new(
        "item_quantity_out_of_range",
        "Every item instance's quantity is positive and within its definition's stack limit and its equipment slot's limit.",
        AuditSql.Counted(
            ItemCount,
            """
            select
                item.id as item_instance_id,
                item.definition_id,
                item.quantity,
                definition.maximum_stack_size,
                equipment_slot.maximum_quantity as slot_maximum_quantity
            from item_instances item
            join item_definitions definition on definition.id = item.definition_id
            left join item_container_slots slot
              on slot.container_id = item.container_id
             and slot.slot_index = item.container_slot_index
            left join equipment_slots equipment_slot
              on equipment_slot.id = coalesce(item.equipment_slot_id, slot.equipment_slot_id)
            where item.quantity < 1
               or item.quantity > definition.maximum_stack_size
               or item.quantity > equipment_slot.maximum_quantity
            """,
            "item_instance_id"));

    // The service runs the catalog that its seeder mirrored: a start refuses a database whose
    // mirror differs from the bundled catalog, and the mirror marks a definition active exactly
    // while the current revision holds it.
    public static SqlAuditCheck DefinitionUnknown { get; } = new(
        "item_definition_unknown",
        "Every item instance's definition is active in the current item catalog revision that the service runs with.",
        AuditSql.Counted(
            ItemCount,
            """
            select
                item.id as item_instance_id,
                item.definition_id,
                definition.catalog_revision,
                definition.is_active
            from item_instances item
            join item_definitions definition on definition.id = item.definition_id
            left join item_catalog_revisions revision
              on revision.catalog_id = definition.catalog_id
             and revision.revision = definition.catalog_revision
             and revision.is_current
            where not definition.is_active
               or revision.catalog_id is null
            """,
            "item_instance_id"));

    public static IReadOnlyList<IAuditCheck> All { get; } =
    [
        CustodyMissing,
        CustodyDuplicated,
        SlotInvalid,
        SlotShared,
        QuantityOutOfRange,
        DefinitionUnknown
    ];
}
