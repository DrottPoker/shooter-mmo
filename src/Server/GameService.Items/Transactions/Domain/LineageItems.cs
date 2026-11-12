using Dapper;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Contracts;

// The source that gave an item its lineage: the source kind and id of its active
// protected_on_death policy, such as a quest grant and its quest.
public sealed record ItemLineage(string SourceKind, string SourceId);

// Items with a lineage: protected items whose active protected_on_death policy names the
// lineage's source. The source grants them, and ending it removes them wherever the character
// owns them, also from Bank and Recovery Storage.
public static class LineageItems
{
    // The quantity of one definition with the lineage that the character owns anywhere.
    public static Task<int> CountOwnedAsync(
        ItemTransactionContext context,
        Guid characterId,
        ItemLineage lineage,
        string definitionId,
        CancellationToken cancellationToken)
    {
        return CountOwnedAsync(context.Scope, characterId, lineage, definitionId, cancellationToken);
    }

    internal static Task<int> CountOwnedAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        ItemLineage lineage,
        string definitionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(lineage);
        return scope.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
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
                LineageId = lineage.SourceId,
                DefinitionId = definitionId,
                lineage.SourceKind,
                PolicyKind = ItemPolicyIds.ProtectedOnDeath,
                ActiveStatus = ItemPolicyRules.ActiveStatus
            },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }

    // The part of an offered quantity of a definition that the character still lacks: the
    // wanted total of the lineage less what the character owns with it, at most the quantity.
    // When nothing is wanted, nothing is read.
    internal static async Task<int> LackingAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        ItemLineage lineage,
        string definitionId,
        int wanted,
        int quantity,
        CancellationToken cancellationToken)
    {
        if (wanted <= 0)
        {
            return 0;
        }

        var owned = await CountOwnedAsync(scope, characterId, lineage, definitionId, cancellationToken);
        return Math.Clamp(wanted - owned, 0, quantity);
    }

    // Removes every item of the quest item category with the lineage, recording the
    // destruction reason, and returns how many items it removed.
    public static async Task<int> RemoveAllAsync(
        ItemTransactionContext context,
        Guid characterId,
        ItemLineage lineage,
        string destructionReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ItemDestructions.EnsureReason(destructionReason);
        var parameters = new
        {
            CharacterId = characterId,
            QuestGrantId = lineage.SourceId,
            lineage.SourceKind,
            QuestItemCategoryId = ItemCategoryIds.QuestItem,
            PolicyKind = ItemPolicyIds.ProtectedOnDeath,
            ActiveStatus = ItemPolicyRules.ActiveStatus
        };
        var itemIds = (await context.Connection.QueryAsync<Guid>(new CommandDefinition(
            LineageItemIdsSql,
            parameters,
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();
        await context.Locks.LockMutationScopeAsync(itemIds, [], cancellationToken);
        itemIds = (await context.Connection.QueryAsync<Guid>(new CommandDefinition(
            LineageItemIdsSql,
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
            await ItemDestructions.DestroyAsync(context, item, destructionReason, cancellationToken);
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

    private const string LineageItemIdsSql =
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
