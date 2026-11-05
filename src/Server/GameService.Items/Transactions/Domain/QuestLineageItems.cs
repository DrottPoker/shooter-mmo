using Dapper;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Items with a quest's lineage: protected quest items whose active protected_on_death policy
// names the quest as its quest_grant source. The quest grants them, and ending the quest
// removes them wherever the character owns them, also from Bank and Recovery Storage.
internal static class QuestLineageItems
{
    // The quantity of one definition with the lineage that the character owns anywhere.
    public static Task<int> CountOwnedAsync(
        ItemTransactionContext context,
        Guid characterId,
        string lineageId,
        string definitionId,
        CancellationToken cancellationToken)
    {
        return context.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
            select coalesce(sum(item.quantity), 0)::integer
            from item_instances item
            {ItemOwnershipSql.Joins}
            join item_instance_policies policy
              on policy.item_instance_id = item.id
             and policy.policy_kind = @PolicyKind
             and policy.status = @ActiveStatus
             and policy.source_kind = @SourceKind
            where {ItemOwnershipSql.OwningCharacterId} = @CharacterId
              and item.definition_id = @DefinitionId
              and policy.source_id = @LineageId;
            """,
            new
            {
                CharacterId = characterId,
                LineageId = lineageId,
                DefinitionId = definitionId,
                SourceKind = ItemPolicySourceKinds.QuestGrant,
                PolicyKind = ItemPolicyIds.ProtectedOnDeath,
                ActiveStatus = ItemPolicyRules.ActiveStatus
            },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    // Removes every item with the lineage of an abandoned quest and returns how many items it
    // removed.
    public static Task<int> RemoveAbandonedAsync(
        ItemTransactionContext context,
        Guid characterId,
        string lineageId,
        CancellationToken cancellationToken)
    {
        return RemoveAllAsync(context, characterId, lineageId, DestroyAbandonedSql, cancellationToken);
    }

    // Removes what is left with the lineage of a turned-in quest, such as spare quest drops.
    public static Task<int> RemoveCompletedAsync(
        ItemTransactionContext context,
        Guid characterId,
        string lineageId,
        CancellationToken cancellationToken)
    {
        return RemoveAllAsync(context, characterId, lineageId, DestroyCompletedSql, cancellationToken);
    }

    private static async Task<int> RemoveAllAsync(
        ItemTransactionContext context,
        Guid characterId,
        string lineageId,
        string destroySql,
        CancellationToken cancellationToken)
    {
        var parameters = new
        {
            CharacterId = characterId,
            QuestGrantId = lineageId,
            SourceKind = ItemPolicySourceKinds.QuestGrant,
            QuestItemCategoryId = ItemCategoryIds.QuestItem,
            PolicyKind = ItemPolicyIds.ProtectedOnDeath,
            ActiveStatus = ItemPolicyRules.ActiveStatus
        };
        var itemIds = (await context.Connection.QueryAsync<Guid>(new CommandDefinition(
            QuestGrantItemIdsSql,
            parameters,
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();
        await context.Locks.LockMutationScopeAsync(itemIds, [], cancellationToken);
        itemIds = (await context.Connection.QueryAsync<Guid>(new CommandDefinition(
            QuestGrantItemIdsSql,
            parameters,
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();

        foreach (var itemInstanceId in itemIds.Order())
        {
            var item = await context.State.LoadItemAsync(itemInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Quest item '{itemInstanceId}' disappeared while its owner was locked.");
            var beforeState = await context.Journal.CaptureItemStateJsonAsync(
                item.ItemInstanceId,
                cancellationToken);
            await context.Connection.ExecuteAsync(new CommandDefinition(
                destroySql,
                new
                {
                    item.ItemInstanceId,
                    item.DefinitionId,
                    context.OperationId,
                    item.Quantity
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            if (string.Equals(item.SourceContainerType, "recovery_storage", StringComparison.Ordinal))
            {
                // Recovery items own their slot row, and a delivery that loses its last
                // item can never be claimed, so both are removed with the item.
                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    delete from item_container_slots
                    where container_id = @ContainerId
                      and slot_index = @SlotIndex;

                    delete from recovery_deliveries delivery
                    where delivery.recovery_storage_container_id = @ContainerId
                      and delivery.claimed_at is null
                      and not exists (
                          select 1
                          from recovery_delivery_items link
                          where link.recovery_delivery_id = delivery.id);
                    """,
                    new
                    {
                        item.ContainerId,
                        SlotIndex = item.ContainerSlotIndex
                    },
                    context.Transaction,
                    cancellationToken: cancellationToken));
            }

            context.Journal.AddItemAudit("quest_item_removed", item.ItemInstanceId, beforeState, null);
            context.Journal.TouchContainer(item.ContainerId);
        }

        return itemIds.Length;
    }

    // The destruction reason is part of the statement text, so every reason has its own.
    private const string DestroyAbandonedSql =
        """
        insert into item_destructions (
            item_instance_id,
            definition_id,
            source_operation_id,
            quantity,
            reason)
        values (
            @ItemInstanceId,
            @DefinitionId,
            @OperationId,
            @Quantity,
            'quest_abandoned');

        delete from item_instances
        where id = @ItemInstanceId;
        """;

    private const string DestroyCompletedSql =
        """
        insert into item_destructions (
            item_instance_id,
            definition_id,
            source_operation_id,
            quantity,
            reason)
        values (
            @ItemInstanceId,
            @DefinitionId,
            @OperationId,
            @Quantity,
            'quest_completed');

        delete from item_instances
        where id = @ItemInstanceId;
        """;

    private const string QuestGrantItemIdsSql =
        $"""
        select item.id
        from item_instances item
        join item_definitions definition
          on definition.id = item.definition_id
         and definition.category_id = @QuestItemCategoryId
        {ItemOwnershipSql.Joins}
        join item_instance_policies policy
          on policy.item_instance_id = item.id
         and policy.policy_kind = @PolicyKind
         and policy.status = @ActiveStatus
         and policy.source_kind = @SourceKind
        where {ItemOwnershipSql.OwningCharacterId} = @CharacterId
          and policy.source_id = @QuestGrantId
        order by item.id;
        """;
}
