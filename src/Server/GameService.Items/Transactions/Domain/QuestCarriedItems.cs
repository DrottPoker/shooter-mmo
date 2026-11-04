using Dapper;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// The items that count for a collect or deliver objective: carried stacks of the definition in
// the permanent inventory and the equipped Bag, never the Bank, Recovery Storage, or equipment.
// An ordinary item counts when it carries no active policy, and a quest item when it carries
// the quest's lineage.
internal static class QuestCarriedItems
{
    public static async Task<int> CountAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        string questId,
        ResolvedItemDefinition definition,
        CancellationToken cancellationToken)
    {
        return (await ListAsync(context, character, questId, definition, cancellationToken))
            .Sum(stack => stack.Quantity);
    }

    // Takes the quantity from the counted stacks, the permanent inventory first, all or nothing.
    public static async Task TakeAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        string questId,
        ResolvedItemDefinition definition,
        int quantity,
        string auditPrefix,
        CancellationToken cancellationToken)
    {
        var stacks = await ListAsync(context, character, questId, definition, cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            stacks.Select(stack => stack.ItemInstanceId),
            [],
            cancellationToken);
        stacks = await ListAsync(context, character, questId, definition, cancellationToken);
        if (stacks.Sum(stack => stack.Quantity) < quantity)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.QuestItemsMissing,
                "The character does not carry enough of the items the quest asks for.");
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
    }

    private static async Task<IReadOnlyList<CarriedStack>> ListAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        string questId,
        ResolvedItemDefinition definition,
        CancellationToken cancellationToken)
    {
        var bagContentsContainerId = await CarriedAcquisition.FindEquippedBagContentsAsync(
            context,
            character,
            cancellationToken);
        Guid[] containerIds = bagContentsContainerId is null
            ? [character.PermanentInventoryContainerId]
            : [character.PermanentInventoryContainerId, bagContentsContainerId.Value];
        var questItem = string.Equals(
            definition.RuntimeDefinition.Category,
            ItemCategoryIds.QuestItem,
            StringComparison.Ordinal);
        return (await context.Connection.QueryAsync<CarriedStack>(new CommandDefinition(
                questItem ? LineageStacksSql : PolicyFreeStacksSql,
                new
                {
                    ContainerIds = containerIds,
                    character.PermanentInventoryContainerId,
                    DefinitionId = definition.RuntimeDefinition.Id,
                    QuestId = questId,
                    SourceKind = ItemPolicySourceKinds.QuestGrant,
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
