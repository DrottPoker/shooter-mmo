using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
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

        var beforeState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        if (quantity == item.Quantity)
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                "delete from item_instances where id = @ItemInstanceId;",
                new { item.ItemInstanceId },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.Journal.AddItemAudit(
                consumedAuditKind,
                item.ItemInstanceId,
                beforeState,
                null);
        }
        else
        {
            await ReduceAsync(context, item, quantity, beforeState, partlyConsumedAuditKind, cancellationToken);
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
        var beforeState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        if (quantity == item.Quantity)
        {
            await ItemDestructions.DestroyAsync(context, item, destructionReason, cancellationToken);
            context.Journal.AddItemAudit(
                removedAuditKind,
                item.ItemInstanceId,
                beforeState,
                null);
        }
        else
        {
            await ReduceAsync(context, item, quantity, beforeState, partlyRemovedAuditKind, cancellationToken);
        }

        context.Journal.TouchContainer(item.ContainerId);
    }

    private static async Task ReduceAsync(
        ItemTransactionContext context,
        LockedItem item,
        int quantity,
        string? beforeState,
        string auditKind,
        CancellationToken cancellationToken)
    {
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instances
            set quantity = quantity - @Quantity,
                revision = revision + 1,
                updated_at = now()
            where id = @ItemInstanceId;
            """,
            new { Quantity = quantity, item.ItemInstanceId },
            context.Transaction,
            cancellationToken: cancellationToken));
        var afterState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            auditKind,
            item.ItemInstanceId,
            beforeState,
            afterState);
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
