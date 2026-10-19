namespace Mmorpg.GameService.Items.Transactions.Domain;

// Mob loot rules. Ids derived from Mob corpse and grant ids use ScopedGuid.
internal static class MobLootRules
{
    public static void ValidateMobLootDefinition(
        ResolvedItemDefinition definition,
        int quantity)
    {
        if (quantity <= 0
            || quantity > definition.RuntimeDefinition.MaximumStackSize
            || definition.IsBag
            || definition.RuntimeDefinition.DefaultPolicies.Length > 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobLootGrantInvalid,
                "Mob loot must be transferable, policy-free, non-Bag item content within its stack limit.");
        }
    }

    // A grant into an equipment slot names no container: no id, revision 0, and slot -1.
    public static bool NamesEquipmentDestinationOnly(
        Guid destinationContainerId,
        long expectedDestinationContainerRevision,
        int destinationSlotIndex)
    {
        return destinationContainerId == Guid.Empty
            && expectedDestinationContainerRevision == 0
            && destinationSlotIndex == -1;
    }
}
