using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Development;

// Checks a package against the item catalog before anything is granted. Every grant commits
// its own transaction, so a package that a catalog change made stale must be rejected while
// the character is still empty. The transaction kernel still enforces every rule.
internal static class DevelopmentItemPackagePlanner
{
    // Returns every problem found. An empty list means each grant fits the catalog and the
    // package reaches its expected load exactly.
    public static IReadOnlyList<string> FindProblems(
        DevelopmentItemPackage package,
        ItemCatalogRuntimeDocument catalog,
        long baseCarryCapacity)
    {
        var definitions = catalog.Definitions.ToDictionary(
            definition => definition.Id,
            StringComparer.Ordinal);
        var slots = catalog.EquipmentSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        var blockedSlotsByOccupiedSlot = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var problems = new List<string>();
        var carriedWeight = 0L;
        var carryCapacity = baseCarryCapacity;
        foreach (var grant in package.Grants)
        {
            if (!definitions.TryGetValue(grant.DefinitionId, out var definition))
            {
                problems.Add($"'{grant.DefinitionId}' is not an active item definition");
                continue;
            }

            if (grant.Quantity > definition.MaximumStackSize)
            {
                problems.Add(
                    $"{grant.Quantity} x '{grant.DefinitionId}' exceeds its stack size {definition.MaximumStackSize}");
            }

            if (grant.Destination == DevelopmentItemPackageDestinations.Equipment)
            {
                AddEquipmentProblems(grant, definition, slots, blockedSlotsByOccupiedSlot, problems);
                carryCapacity += definition.Bag?.CarryCapacityBonus ?? 0;
            }
            else if (DevelopmentItemPackageDestinations.IsCarried(grant.Destination))
            {
                carriedWeight += ItemWeightRules.CalculateStackWeight(definition.UnitWeight, grant.Quantity);
            }
        }

        if (problems.Count == 0
            && package.ExpectedLoadPercent is int expectedLoadPercent
            && carriedWeight * 100 != carryCapacity * expectedLoadPercent)
        {
            problems.Add(
                $"it reaches {carriedWeight}/{carryCapacity} instead of exactly {expectedLoadPercent} percent");
        }

        return problems;
    }

    private static void AddEquipmentProblems(
        DevelopmentItemPackageGrant grant,
        ItemDefinition definition,
        IReadOnlyDictionary<string, ItemEquipmentSlotEntry> slots,
        Dictionary<string, string[]> blockedSlotsByOccupiedSlot,
        List<string> problems)
    {
        var slotId = grant.EquipmentSlotId!;
        if (!slots.TryGetValue(slotId, out var slot)
            || !ItemEquipmentRules.IsCompatible(definition, slotId))
        {
            problems.Add($"'{grant.DefinitionId}' cannot be equipped into '{slotId}'");
            return;
        }

        if (!ItemEquipmentRules.CanHoldQuantity(definition, slot, grant.Quantity))
        {
            problems.Add(
                $"'{slotId}' holds at most {ItemEquipmentRules.MaximumQuantity(definition, slot)} x '{grant.DefinitionId}'");
        }

        var blockingSlot = blockedSlotsByOccupiedSlot.ContainsKey(slotId)
            ? slotId
            : ItemEquipmentRules.FindBlockingSlot(definition, slotId, blockedSlotsByOccupiedSlot);
        if (blockingSlot is not null)
        {
            problems.Add(
                $"'{grant.DefinitionId}' in '{slotId}' conflicts with the item equipped in '{blockingSlot}'");
        }

        blockedSlotsByOccupiedSlot[slotId] = definition.BlockedEquipmentSlots ?? [];
    }
}
