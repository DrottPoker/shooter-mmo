using Mmorpg.GameService.Items.Transactions;

namespace Mmorpg.GameService.Items.Audit;

// What each operation kind of Items may do to the total quantity of a definition, for the
// journal's conservation check. Every kind of ItemOperationKinds is in exactly one list, which
// ItemAuditTests pins. The kinds of the modules above Items are not classified, so the check does
// not judge them.
internal static class ItemOperationQuantityEffects
{
    // Moves, splits, merges, swaps, equips, claims, and policy changes keep every total.
    public static IReadOnlyList<string> Moving { get; } =
    [
        ItemOperationKinds.Relocate,
        ItemOperationKinds.SwapContainerItems,
        ItemOperationKinds.Equip,
        ItemOperationKinds.Unequip,
        ItemOperationKinds.SplitStack,
        ItemOperationKinds.MergeStacks,
        ItemOperationKinds.SwapBagAggregates,
        ItemOperationKinds.AddRecoveryDelivery,
        ItemOperationKinds.ClaimRecoveryDelivery,
        ItemOperationKinds.ApplyItemPolicy,
        ItemOperationKinds.RemoveInsurancePolicy,
        ItemOperationKinds.LootCorpseItem,
        ItemOperationKinds.LootCorpsePartialStack,
        ItemOperationKinds.LootCorpseItemToEquipment,
        ItemOperationKinds.DepositCorpseItem,
        ItemOperationKinds.DepositCorpsePartialStack,
        ItemOperationKinds.MoveCorpseItem,
        ItemOperationKinds.MoveCorpsePartialStack,
        ItemOperationKinds.SwapCorpseBag,
        ItemOperationKinds.LootAllCorpseItems
    ];

    // Grants and Mob loot create items and never take any.
    public static IReadOnlyList<string> Creating { get; } =
    [
        ItemOperationKinds.Grant,
        ItemOperationKinds.CreatePersistentMobCorpse,
        ItemOperationKinds.GrantMobLoot,
        ItemOperationKinds.GrantMobLootAll
    ];

    // Destruction, consumption, and expiry take items and never create any. A death moves its
    // items and spends the arrows that were shot but not yet committed.
    public static IReadOnlyList<string> Removing { get; } =
    [
        ItemOperationKinds.Destroy,
        ItemOperationKinds.ConsumeQuantity,
        ItemOperationKinds.ConsumeEquippedAmmunition,
        ItemOperationKinds.ExpireCorpse,
        ItemOperationKinds.ExpireRecoveryDelivery,
        ItemOperationKinds.ProcessPlayerDeath
    ];
}
