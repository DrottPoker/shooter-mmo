using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Contracts;

// Removes a quantity from a locked item: the whole item when nothing remains, otherwise a
// smaller stack with a new revision. Every consumption and sale path shares these writes.
public static class ItemQuantityConsumption
{
    internal static async Task ApplyAsync(
        ItemTransactionContext context,
        LockedItem item,
        int quantity,
        string consumedAuditKind,
        string partlyConsumedAuditKind,
        CancellationToken cancellationToken)
    {
        if (quantity <= 0 || quantity > item.Quantity)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemQuantityChanged,
                "The consume quantity exceeds the current item quantity.");
        }

        var consume = new PostgresBatch(context.Scope);
        var beforeState = context.Journal.CaptureItemStateJson(consume, item.ItemInstanceId);
        if (quantity == item.Quantity)
        {
            consume.Execute(
                "delete from item_instances where id = @ItemInstanceId;",
                new { item.ItemInstanceId });
            await consume.ExecuteAsync(cancellationToken);
            context.Journal.AddItemAudit(
                consumedAuditKind,
                item.ItemInstanceId,
                beforeState.Value,
                null);
        }
        else
        {
            await ReduceAsync(context, consume, item, quantity, beforeState, partlyConsumedAuditKind, cancellationToken);
        }

        context.Journal.TouchContainer(item.ContainerId);
    }

    // Removes a quantity of a locked item that the caller already checked against the item, for
    // a reason that a whole item records in item_destructions, such as a vendor sale. A part
    // leaves a smaller stack with a new revision.
    public static async Task RemoveForReasonAsync(
        ItemTransactionContext context,
        LockedItem item,
        int quantity,
        string destructionReason,
        string removedAuditKind,
        string partlyRemovedAuditKind,
        CancellationToken cancellationToken)
    {
        ItemDestructions.EnsureReason(destructionReason);
        var remove = new PostgresBatch(context.Scope);
        var beforeState = context.Journal.CaptureItemStateJson(remove, item.ItemInstanceId);
        if (quantity == item.Quantity)
        {
            // The destruction is one command of two statements and keeps its own round trip.
            await remove.ExecuteAsync(cancellationToken);
            await ItemDestructions.DestroyAsync(context, item, destructionReason, cancellationToken);
            context.Journal.AddItemAudit(
                removedAuditKind,
                item.ItemInstanceId,
                beforeState.Value,
                null);
        }
        else
        {
            await ReduceAsync(context, remove, item, quantity, beforeState, partlyRemovedAuditKind, cancellationToken);
        }

        context.Journal.TouchContainer(item.ContainerId);
    }

    // The smaller stack and its audit state after the read of its state before, in one round trip.
    private static async Task ReduceAsync(
        ItemTransactionContext context,
        PostgresBatch reduce,
        LockedItem item,
        int quantity,
        PostgresBatchResult<string?> beforeState,
        string auditKind,
        CancellationToken cancellationToken)
    {
        reduce.Execute(
            """
            update item_instances
            set quantity = quantity - @Quantity,
                revision = revision + 1,
                updated_at = now()
            where id = @ItemInstanceId;
            """,
            new { Quantity = quantity, item.ItemInstanceId });
        var afterState = context.Journal.CaptureItemStateJson(reduce, item.ItemInstanceId);
        await reduce.ExecuteAsync(cancellationToken);
        context.Journal.AddItemAudit(
            auditKind,
            item.ItemInstanceId,
            beforeState.Value,
            afterState.Value);
        context.Journal.IncludeResultItem(item.ItemInstanceId);
    }

    // Shots the SimulationWorker counted against the character's equipped ammunition. The
    // item must still be equipped ammunition of that character.
    internal static async Task ConsumeEquippedAmmunitionAsync(
        ItemTransactionContext context,
        Guid characterId,
        LockedItem item,
        int quantity,
        CancellationToken cancellationToken)
    {
        var definition = await context.State.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
        if (item.EquippedCharacterId != characterId
            || !string.Equals(
                definition.RuntimeDefinition.Category,
                ItemCategoryIds.Ammunition,
                StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.AmmunitionNotEquipped,
                "The item is not ammunition that the character has equipped.");
        }

        await ApplyAsync(
            context,
            item,
            quantity,
            "ammunition_consumed",
            "ammunition_quantity_consumed",
            cancellationToken);
    }
}
