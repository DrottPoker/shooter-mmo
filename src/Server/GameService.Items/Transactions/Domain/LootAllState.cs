using Dapper;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Reads what CorpseLootAllPlanner plans with, inside a Loot all transaction that already
// holds the character lock: the looter's equipment and carried slots, its level, and the
// definitions every planned item needs.
internal sealed class LootAllState
{
    private readonly ItemTransactionContext context;
    private readonly Dictionary<string, ResolvedItemDefinition> definitions = new(StringComparer.Ordinal);
    private int? characterLevel;

    public LootAllState(ItemTransactionContext context)
    {
        this.context = context;
    }

    public IReadOnlyDictionary<string, ResolvedItemDefinition> Definitions => definitions;

    public async Task<ResolvedItemDefinition> LoadDefinitionAsync(
        string definitionId,
        CancellationToken cancellationToken)
    {
        if (!definitions.TryGetValue(definitionId, out var definition))
        {
            definition = await context.State.LoadDefinitionAsync(definitionId, cancellationToken);
            definitions.Add(definitionId, definition);
        }

        return definition;
    }

    public ItemDefinition? FindDefinition(string definitionId)
    {
        return definitions.TryGetValue(definitionId, out var definition) ? definition.RuntimeDefinition : null;
    }

    public bool MeetsLevelRequirement(ItemDefinition definition)
    {
        return definition.RequiredCharacterLevel <= 1 || characterLevel >= definition.RequiredCharacterLevel;
    }

    public async Task<LootAllLooter> LoadLooterAsync(
        LockedCharacterState character,
        CancellationToken cancellationToken)
    {
        characterLevel = await context.Placement.LoadCharacterLevelAsync(character.CharacterId, cancellationToken);
        var equipmentRows = (await context.Connection.QueryAsync<EquipmentRow>(new CommandDefinition(
            """
            select
                equipment.id as "SlotId",
                equipment.display_name as "DisplayName",
                equipment.maximum_quantity as "MaximumQuantity",
                item.id as "ItemInstanceId",
                item.definition_id as "DefinitionId",
                item.quantity as "Quantity",
                bag_contents.id as "BagContentsContainerId"
            from equipment_slots equipment
            left join item_instances item
              on item.equipped_character_id = @CharacterId
             and item.equipment_slot_id = equipment.id
            left join item_containers bag_contents
              on bag_contents.bound_bag_item_instance_id = item.id
             and bag_contents.container_type = 'bag_contents'
             and bag_contents.lifecycle = 'active'
             and equipment.id = @BagSlotId
            where equipment.is_active
            order by equipment.display_order, equipment.id;
            """,
            new { character.CharacterId, BagSlotId = ItemEquipmentSlotIds.Bag },
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();
        var permanentInventory = await LoadSlotsAsync(
            character.PermanentInventoryContainerId,
            ItemContainerTypeIds.PermanentInventory,
            cancellationToken);
        var bagContentsContainerId = equipmentRows
            .Select(row => row.BagContentsContainerId)
            .FirstOrDefault(id => id is not null);
        IReadOnlyList<LootAllCarriedSlot> bagContents = bagContentsContainerId is Guid bagId
            ? await LoadSlotsAsync(bagId, ItemContainerTypeIds.BagContents, cancellationToken)
            : [];

        var equipment = new List<LootAllEquipmentSlot>(equipmentRows.Length);
        foreach (var row in equipmentRows)
        {
            LootAllStack? occupant = null;
            if (row.ItemInstanceId is Guid itemId)
            {
                await LoadDefinitionAsync(row.DefinitionId!, cancellationToken);
                occupant = await ToStackAsync(itemId, row.DefinitionId!, row.Quantity!.Value, cancellationToken);
            }

            equipment.Add(new LootAllEquipmentSlot(
                new ItemEquipmentSlotEntry
                {
                    Id = row.SlotId,
                    DisplayName = row.DisplayName,
                    MaximumQuantity = row.MaximumQuantity
                },
                occupant));
        }

        return new LootAllLooter(
            equipment,
            permanentInventory,
            bagContents,
            bagContentsContainerId,
            permanentInventory.Concat(bagContents)
                .Where(slot => slot.Occupant is not null)
                .Select(slot => slot.Occupant!.ItemInstanceId)
                .Concat(equipment.Where(slot => slot.Occupant is not null).Select(slot => slot.Occupant!.ItemInstanceId))
                .ToArray());
    }

    // The slots of a container in slot order, with their accepted tags and occupants.
    public async Task<IReadOnlyList<LootAllCarriedSlot>> LoadSlotsAsync(
        Guid containerId,
        string containerType,
        CancellationToken cancellationToken)
    {
        var rows = (await context.Connection.QueryAsync<SlotRow>(new CommandDefinition(
            """
            select
                slot.slot_index as "SlotIndex",
                slot.slot_kind as "SlotKind",
                tag.tag_id as "TagId",
                item.id as "ItemInstanceId",
                item.definition_id as "DefinitionId",
                item.quantity as "Quantity"
            from item_container_slots slot
            left join item_container_slot_tags tag
              on tag.container_id = slot.container_id
             and tag.slot_index = slot.slot_index
            left join item_instances item
              on item.container_id = slot.container_id
             and item.container_slot_index = slot.slot_index
            where slot.container_id = @ContainerId
            order by slot.slot_index, tag.tag_id;
            """,
            new { ContainerId = containerId },
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();
        var slots = new List<LootAllCarriedSlot>();
        foreach (var group in rows.GroupBy(row => row.SlotIndex))
        {
            var first = group.First();
            LootAllStack? occupant = null;
            if (first.ItemInstanceId is Guid itemId)
            {
                await LoadDefinitionAsync(first.DefinitionId!, cancellationToken);
                occupant = await ToStackAsync(itemId, first.DefinitionId!, first.Quantity!.Value, cancellationToken);
            }

            slots.Add(new LootAllCarriedSlot(
                containerId,
                containerType,
                group.Key,
                first.SlotKind,
                group.Where(row => row.TagId is not null).Select(row => row.TagId!),
                occupant));
        }

        return slots;
    }

    public async Task<LootAllStack> ToStackAsync(
        Guid itemInstanceId,
        string definitionId,
        int quantity,
        CancellationToken cancellationToken)
    {
        var policies = await context.State.LoadPoliciesAsync(itemInstanceId, cancellationToken);
        return new LootAllStack(
            itemInstanceId,
            definitionId,
            quantity,
            MergeKey(definitions[definitionId], policies));
    }

    // Stacks merge when their active policies and stack state match, as GameService compares
    // them, and only items whose policies allow stacking merge at all.
    public static string? MergeKey(
        ResolvedItemDefinition definition,
        IReadOnlyList<LockedItemPolicy> policies)
    {
        var capabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ItemPolicyStates.ToPolicyStates(policies));
        if (!capabilities.CanStack)
        {
            return null;
        }

        return ItemPolicyStates.CalculatePolicyFingerprint(policies.Where(policy => string.Equals(
                policy.Status,
                ItemPolicyRules.ActiveStatus,
                StringComparison.Ordinal)))
            + "\n" + ItemPolicyStates.EmptyStackStateFingerprint;
    }

    private sealed class EquipmentRow
    {
        public string SlotId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public int MaximumQuantity { get; set; }

        public Guid? ItemInstanceId { get; set; }

        public string? DefinitionId { get; set; }

        public int? Quantity { get; set; }

        public Guid? BagContentsContainerId { get; set; }
    }

    private sealed class SlotRow
    {
        public int SlotIndex { get; set; }

        public string SlotKind { get; set; } = string.Empty;

        public string? TagId { get; set; }

        public Guid? ItemInstanceId { get; set; }

        public string? DefinitionId { get; set; }

        public int? Quantity { get; set; }
    }
}

// The looter's side of a Loot all plan.
internal sealed record LootAllLooter(
    IReadOnlyList<LootAllEquipmentSlot> Equipment,
    IReadOnlyList<LootAllCarriedSlot> PermanentInventory,
    IReadOnlyList<LootAllCarriedSlot> BagContents,
    Guid? BagContentsContainerId,
    IReadOnlyList<Guid> ItemInstanceIds);
