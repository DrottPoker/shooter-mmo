using Dapper;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Places a new quantity of one definition into carried storage when no client chose a
// slot, as for a vendor purchase or a gathered yield. It tops up existing stacks in the
// permanent inventory and then in the equipped Bag, and fills empty slots in this order:
// matching specialized Bag slots, the permanent inventory, then general Bag slots. The
// Secure Container is never filled automatically. The placement is all or nothing, and
// the finalizer enforces the carry-weight cap.
internal static class CarriedAcquisition
{
    public static async Task AcquireAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        ResolvedItemDefinition definition,
        int quantity,
        string auditPrefix,
        CancellationToken cancellationToken)
    {
        var runtime = definition.RuntimeDefinition;
        if (quantity <= 0 || quantity > runtime.MaximumStackSize)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStackLimitExceeded,
                "An acquired quantity must be positive and fit one stack.");
        }

        if (runtime.DefaultPolicies.Length > 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "Items with default policies are never acquired into carried storage this way.");
        }

        var bagContentsContainerId = await context.Connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                select container.id
                from item_instances bag
                join item_containers container
                  on container.bound_bag_item_instance_id = bag.id
                 and container.container_type = 'bag_contents'
                 and container.lifecycle = 'active'
                where bag.equipped_character_id = @CharacterId
                  and bag.equipment_slot_id = @BagSlotId;
                """,
                new { character.CharacterId, BagSlotId = ItemEquipmentSlotIds.Bag },
                context.Transaction,
                cancellationToken: cancellationToken));
        Guid[] containerIds = bagContentsContainerId is null
            ? [character.PermanentInventoryContainerId]
            : [character.PermanentInventoryContainerId, bagContentsContainerId.Value];

        // Only policy-free stacks with room merge, because new items carry no policy.
        var stacks = runtime.MaximumStackSize <= 1
            ? []
            : (await context.Connection.QueryAsync<StackRow>(new CommandDefinition(
                """
                select
                    item.id as "ItemInstanceId",
                    item.container_id as "ContainerId",
                    item.quantity as "Quantity"
                from item_instances item
                where item.container_id = any(@ContainerIds)
                  and item.definition_id = @DefinitionId
                  and item.quantity < @MaximumStackSize
                  and not exists (
                      select 1
                      from item_instance_policies policy
                      where policy.item_instance_id = item.id
                        and policy.status = 'active')
                order by
                    case when item.container_id = @PermanentInventoryContainerId then 0 else 1 end,
                    item.container_slot_index;
                """,
                new
                {
                    ContainerIds = containerIds,
                    DefinitionId = runtime.Id,
                    runtime.MaximumStackSize,
                    character.PermanentInventoryContainerId
                },
                context.Transaction,
                cancellationToken: cancellationToken))).ToArray();

        await context.Locks.LockMutationScopeAsync(
            stacks.Select(stack => stack.ItemInstanceId),
            containerIds,
            cancellationToken);

        var remaining = quantity;
        foreach (var stack in stacks)
        {
            if (remaining == 0)
            {
                break;
            }

            var added = Math.Min(remaining, runtime.MaximumStackSize - stack.Quantity);
            var beforeState = await context.Journal.CaptureItemStateJsonAsync(
                stack.ItemInstanceId,
                cancellationToken);
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_instances
                set quantity = quantity + @Added,
                    revision = revision + 1,
                    updated_at = now()
                where id = @ItemInstanceId;
                """,
                new { Added = added, stack.ItemInstanceId },
                context.Transaction,
                cancellationToken: cancellationToken));
            var afterState = await context.Journal.CaptureItemStateJsonAsync(
                stack.ItemInstanceId,
                cancellationToken);
            context.Journal.AddItemAudit(
                auditPrefix + "_stack_increased",
                stack.ItemInstanceId,
                beforeState,
                afterState);
            context.Journal.TouchContainer(stack.ContainerId);
            context.Journal.IncludeResultItem(stack.ItemInstanceId);
            remaining -= added;
        }

        if (remaining > 0)
        {
            var freeSlots = await ListFreeSlotsAsync(
                context,
                character,
                bagContentsContainerId,
                definition,
                cancellationToken);
            var neededSlots = (remaining + runtime.MaximumStackSize - 1) / runtime.MaximumStackSize;
            if (freeSlots.Count < neededSlots)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.InventoryFull,
                    "Carried storage has no room for the items.");
            }

            foreach (var slot in freeSlots)
            {
                if (remaining == 0)
                {
                    break;
                }

                var stackQuantity = Math.Min(remaining, runtime.MaximumStackSize);
                var itemInstanceId = Guid.NewGuid();
                await NewItemInstances.CreateAsync(
                    context,
                    itemInstanceId,
                    definition,
                    stackQuantity,
                    slot.ContainerId,
                    slot.SlotIndex,
                    ItemPolicySourceKinds.CatalogDefault,
                    runtime.Id,
                    cancellationToken);
                var afterState = await context.Journal.CaptureItemStateJsonAsync(
                    itemInstanceId,
                    cancellationToken);
                context.Journal.AddItemAudit(
                    auditPrefix + "_item_created",
                    itemInstanceId,
                    null,
                    afterState);
                context.Journal.TouchContainer(slot.ContainerId);
                context.Journal.IncludeResultItem(itemInstanceId);
                remaining -= stackQuantity;
            }
        }

        context.Journal.TouchCharacter(character.CharacterId);
    }

    private static async Task<IReadOnlyList<CarriedSlot>> ListFreeSlotsAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        Guid? bagContentsContainerId,
        ResolvedItemDefinition definition,
        CancellationToken cancellationToken)
    {
        var inventory = await context.State.LoadOwnedContainerAsync(
            character.PermanentInventoryContainerId,
            character.CharacterId,
            cancellationToken);
        var inventorySlots = await context.Placement.ListAcceptingSlotsAsync(
            inventory,
            definition,
            cancellationToken);
        if (bagContentsContainerId is null)
        {
            return inventorySlots;
        }

        var bag = await context.State.LoadOwnedContainerAsync(
            bagContentsContainerId.Value,
            character.CharacterId,
            cancellationToken);
        var bagSlots = await context.Placement.ListAcceptingSlotsAsync(
            bag,
            definition,
            cancellationToken);
        return bagSlots
            .Where(slot => string.Equals(slot.SlotKind, BagSlotKindIds.Specialized, StringComparison.Ordinal))
            .Concat(inventorySlots)
            .Concat(bagSlots.Where(slot =>
                !string.Equals(slot.SlotKind, BagSlotKindIds.Specialized, StringComparison.Ordinal)))
            .ToArray();
    }

    private sealed class StackRow
    {
        public Guid ItemInstanceId { get; set; }

        public Guid ContainerId { get; set; }

        public int Quantity { get; set; }
    }
}
