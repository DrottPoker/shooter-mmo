using Dapper;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Contracts;

// The carried stacks of one definition that count for a lineage: stacks in the permanent
// inventory and the equipped Bag, never the Bank, Recovery Storage, or equipment. An ordinary
// item counts when it carries no active policy, and an item of the quest item category when it
// carries the lineage.
public static class CarriedStacks
{
    public static async Task<int> CountAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        ItemLineage lineage,
        ResolvedItemDefinition definition,
        CancellationToken cancellationToken)
    {
        return (await ListAsync(context, character, lineage, definition, cancellationToken))
            .Sum(stack => stack.Quantity);
    }

    // Takes the quantity from the counted stacks, the permanent inventory first, all or nothing.
    // Returns false and takes nothing when the counted stacks hold less.
    public static async Task<bool> TryTakeAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        ItemLineage lineage,
        ResolvedItemDefinition definition,
        int quantity,
        string auditPrefix,
        CancellationToken cancellationToken)
    {
        var stacks = await ListAsync(context, character, lineage, definition, cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            stacks.Select(stack => stack.ItemInstanceId),
            [],
            cancellationToken);
        stacks = await ListAsync(context, character, lineage, definition, cancellationToken);
        if (stacks.Sum(stack => stack.Quantity) < quantity)
        {
            return false;
        }

        var remaining = quantity;
        foreach (var stack in stacks)
        {
            if (remaining == 0)
            {
                break;
            }

            var item = await context.State.LoadItemAsync(stack.ItemInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Quest item '{stack.ItemInstanceId}' disappeared while its owner was locked.");
            var taken = Math.Min(remaining, item.Quantity);
            await ItemQuantityConsumption.ApplyAsync(
                context,
                item,
                taken,
                auditPrefix + "_taken",
                auditPrefix + "_partly_taken",
                cancellationToken);
            remaining -= taken;
        }

        context.Journal.TouchCharacter(character.CharacterId);
        return true;
    }

    private static async Task<IReadOnlyList<CarriedStack>> ListAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        ItemLineage lineage,
        ResolvedItemDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        var bagContentsContainerId = await CarriedAcquisition.FindEquippedBagContentsAsync(
            context,
            character,
            cancellationToken);
        Guid[] containerIds = bagContentsContainerId is null
            ? [character.PermanentInventoryContainerId]
            : [character.PermanentInventoryContainerId, bagContentsContainerId.Value];
        var lineageItem = string.Equals(
            definition.RuntimeDefinition.Category,
            ItemCategoryIds.QuestItem,
            StringComparison.Ordinal);
        return (await context.Connection.QueryAsync<CarriedStack>(new CommandDefinition(
                lineageItem ? LineageStacksSql : PolicyFreeStacksSql,
                new
                {
                    ContainerIds = containerIds,
                    character.PermanentInventoryContainerId,
                    DefinitionId = definition.RuntimeDefinition.Id,
                    QuestId = lineage.SourceId,
                    lineage.SourceKind,
                    PolicyKind = ItemPolicyIds.ProtectedOnDeath,
                    ActiveStatus = ItemPolicyRules.ActiveStatus
                },
                context.Transaction,
                cancellationToken: cancellationToken)))
            .ToArray();
    }

    private const string LineageStacksSql =
        """
        select item.id as "ItemInstanceId",
               item.quantity as "Quantity"
        from item_instances item
        where item.container_id = any(@ContainerIds)
          and item.definition_id = @DefinitionId
          and exists (
              select 1
              from item_instance_policies policy
              where policy.item_instance_id = item.id
                and policy.policy_kind = @PolicyKind
                and policy.status = @ActiveStatus
                and policy.source_kind = @SourceKind
                and policy.source_id = @QuestId)
        order by
            case when item.container_id = @PermanentInventoryContainerId then 0 else 1 end,
            item.container_slot_index,
            item.id;
        """;

    private const string PolicyFreeStacksSql =
        """
        select item.id as "ItemInstanceId",
               item.quantity as "Quantity"
        from item_instances item
        where item.container_id = any(@ContainerIds)
          and item.definition_id = @DefinitionId
          and not exists (
              select 1
              from item_instance_policies policy
              where policy.item_instance_id = item.id
                and policy.status = @ActiveStatus)
        order by
            case when item.container_id = @PermanentInventoryContainerId then 0 else 1 end,
            item.container_slot_index,
            item.id;
        """;

    private sealed record CarriedStack(Guid ItemInstanceId, int Quantity);
}
