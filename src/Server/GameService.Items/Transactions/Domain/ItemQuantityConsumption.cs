using Dapper;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Removes a quantity from a locked item: the whole item when nothing remains, otherwise a
// smaller stack with a new revision. Every consumption path shares this one write.
internal static class ItemQuantityConsumption
{
    public static async Task ApplyAsync(
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
                partlyConsumedAuditKind,
                item.ItemInstanceId,
                beforeState,
                afterState);
            context.Journal.IncludeResultItem(item.ItemInstanceId);
        }

        context.Journal.TouchContainer(item.ContainerId);
    }

    // Shots the SimulationWorker counted against the character's equipped ammunition. The
    // item must still be equipped ammunition of that character.
    public static async Task ConsumeEquippedAmmunitionAsync(
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
