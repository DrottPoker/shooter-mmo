using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Guards for moving a Bag together with its contents between owners.
internal static class BagAggregateGuards
{
    public static async Task EnsureBagAggregateTransferAllowedAsync(
        ItemTransactionContext context,
        LockedItem bag,
        CancellationToken cancellationToken)
    {
        var aggregateItemIds = await context.LoadBagAggregateItemIdsAsync(
            bag.ItemInstanceId,
            cancellationToken);
        foreach (var itemInstanceId in aggregateItemIds)
        {
            var item = itemInstanceId == bag.ItemInstanceId
                ? bag
                : await context.LoadItemAsync(itemInstanceId, cancellationToken);
            if (item is null)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.BagStateChanged,
                    "A Bag aggregate item changed before policy validation completed.");
            }

            var definition = await context.LoadDefinitionAsync(
                item.DefinitionId,
                cancellationToken);
            var policies = await context.LoadPoliciesAsync(
                item.ItemInstanceId,
                cancellationToken);
            var capabilities = ItemPolicyRules.Evaluate(
                definition.RuntimeDefinition,
                ItemPolicyStates.ToPolicyStates(policies));
            if (!capabilities.CanChangeOwningCharacter)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.ItemPolicyRestricted,
                    "An active item policy prevents the Bag aggregate from changing character ownership.");
            }
        }
    }

    public static void EnsureEquippedBagAggregate(
        LockedItem bag,
        Guid expectedCharacterId,
        long expectedContentsRevision)
    {
        if (!bag.IsBag
            || bag.EquippedCharacterId != expectedCharacterId
            || !string.Equals(bag.EquipmentSlotId, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal)
            || bag.BagContentsContainerId is null
            || !string.Equals(bag.BagContentsLifecycle, "active", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The expected equipped Bag aggregate is no longer valid.");
        }

        if (bag.BagContentsRevision != expectedContentsRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The Bag contents changed before the aggregate swap committed.");
        }
    }
}
