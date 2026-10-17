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

}
