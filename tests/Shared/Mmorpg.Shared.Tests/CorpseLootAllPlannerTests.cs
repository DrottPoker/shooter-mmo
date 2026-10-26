using Mmorpg.WorldData.Items;

namespace Mmorpg.Shared.Tests;

public sealed class CorpseLootAllPlannerTests
{
    private const string Key = "plain";
    private static readonly Guid Inventory = Guid.NewGuid();
    private static readonly Guid PlayerBag = Guid.NewGuid();

    private static readonly Dictionary<string, ItemDefinition> Definitions = new(StringComparer.Ordinal)
    {
        ["sword"] = Definition("sword", 50, 1, ["main_hand"]),
        ["bow"] = Definition("bow", 40, 1, ["main_hand"], blocks: ["off_hand"]),
        ["shield"] = Definition("shield", 60, 1, ["off_hand"]),
        ["ring"] = Definition("ring", 1, 1, ["ring_1", "ring_2"]),
        ["helm"] = Definition("helm", 30, 1, ["head"], level: 5),
        ["arrow"] = Definition("arrow", 1, 100, ["ammunition"], tags: ["ammo"]),
        ["ore"] = Definition("ore", 10, 20, []),
        ["pack"] = Definition("pack", 5, 1, ["bag"])
    };

    private static readonly ItemEquipmentSlotEntry[] SlotEntries =
    [
        Slot("head"), Slot("main_hand"), Slot("off_hand"), Slot("ammunition", 50),
        Slot("ring_1"), Slot("ring_2"), Slot("bag")
    ];

    [Fact]
    public void EquipmentGoesIntoFreeSlotsFirstAndTheRestIntoTheInventory()
    {
        var sword = CorpseItem("sword", 1, CorpseSectionKindIds.GeneralInventory);
        var ringA = CorpseItem("ring", 1, CorpseSectionKindIds.Equipment);
        var ringB = CorpseItem("ring", 1, CorpseSectionKindIds.Equipment);
        var ore = CorpseItem("ore", 5, CorpseSectionKindIds.GeneralInventory);

        var plan = Plan(Request([sword, ringA, ringB, ore], equipped: new() { ["main_hand"] = Stack("sword", 1) }));

        Assert.Collection(
            plan.Steps,
            step => AssertStep(step, LootAllStepKind.Equip, ringA, 1, equipmentSlotId: "ring_1"),
            step => AssertStep(step, LootAllStepKind.Equip, ringB, 1, equipmentSlotId: "ring_2"),
            step => AssertPlace(step, sword, 1, Inventory, 0),
            step => AssertPlace(step, ore, 5, Inventory, 1));
    }

    [Fact]
    public void AnEquipBlockedByAnotherItemOrTheLevelGoesIntoTheInventory()
    {
        var bow = CorpseItem("bow", 1, CorpseSectionKindIds.Equipment);
        var helm = CorpseItem("helm", 1, CorpseSectionKindIds.Equipment);

        var plan = Plan(Request([bow, helm], equipped: new() { ["off_hand"] = Stack("shield", 1) }));

        Assert.Collection(
            plan.Steps,
            step => AssertPlace(step, bow, 1, Inventory, 0),
            step => AssertPlace(step, helm, 1, Inventory, 1));
    }

    [Fact]
    public void ArrowsTopUpTheEquippedStackAndTheRestTopsUpCarriedStacks()
    {
        var equippedArrows = Stack("arrow", 45);
        var carriedArrows = Stack("arrow", 90);
        var arrows = CorpseItem("arrow", 30, CorpseSectionKindIds.GeneralInventory);

        var plan = Plan(Request(
            [arrows],
            equipped: new() { ["ammunition"] = equippedArrows },
            inventory: [carriedArrows]));

        Assert.Collection(
            plan.Steps,
            step => AssertStep(step, LootAllStepKind.TopUpEquipped, arrows, 5, takesRest: false, target: equippedArrows.ItemInstanceId),
            step => AssertStep(step, LootAllStepKind.Merge, arrows, 10, takesRest: false, target: carriedArrows.ItemInstanceId),
            step => AssertPlace(step, arrows, 15, Inventory, 1));
    }

    [Fact]
    public void WholeItemsOnlyNeverSplitsAStack()
    {
        var equippedArrows = Stack("arrow", 45);
        var arrows = CorpseItem("arrow", 30, CorpseSectionKindIds.GeneralInventory);

        var plan = Plan(Request(
            [arrows],
            equipped: new() { ["ammunition"] = equippedArrows },
            wholeItemsOnly: true));

        Assert.Collection(plan.Steps, step => AssertPlace(step, arrows, 30, Inventory, 0));
    }

    [Fact]
    public void AFullInventoryTakesOnlyWhatFitsAndLeavesTheRest()
    {
        var ore = CorpseItem("ore", 8, CorpseSectionKindIds.GeneralInventory);
        var sword = CorpseItem("sword", 1, CorpseSectionKindIds.GeneralInventory);
        var carriedOre = Stack("ore", 15);

        var plan = Plan(Request([sword, ore], inventory: [carriedOre], inventorySlots: 1));

        Assert.Collection(
            plan.Steps,
            step => AssertStep(step, LootAllStepKind.Equip, sword, 1, equipmentSlotId: "main_hand"),
            step => AssertStep(step, LootAllStepKind.Merge, ore, 5, takesRest: false, target: carriedOre.ItemInstanceId));
    }

    [Fact]
    public void TheHardCapLimitsTheQuantityAndSkipsWhatIsTooHeavy()
    {
        // Capacity 1000 allows 1400 at the hard cap; 1300 are carried, so 100 weight units fit.
        var shield = CorpseItem("shield", 1, CorpseSectionKindIds.GeneralInventory);
        var sword = CorpseItem("sword", 1, CorpseSectionKindIds.GeneralInventory);
        var ore = CorpseItem("ore", 20, CorpseSectionKindIds.GeneralInventory);

        var plan = Plan(Request(
            [shield, sword, ore],
            equipped: new() { ["main_hand"] = Stack("sword", 1), ["off_hand"] = Stack("shield", 1) },
            weight: 1300,
            capacity: 1000));

        Assert.Collection(
            plan.Steps,
            step => AssertPlace(step, shield, 1, Inventory, 0),
            step => AssertPlace(step, ore, 4, Inventory, 1, takesRest: false));
        Assert.Equal(1400, plan.CarriedWeight);
    }

    [Fact]
    public void SpecializedBagSlotsFillBeforeTheInventoryAndGeneralBagSlotsAfter()
    {
        var arrows = CorpseItem("arrow", 10, CorpseSectionKindIds.GeneralInventory);
        var ore = CorpseItem("ore", 1, CorpseSectionKindIds.GeneralInventory);
        var request = Request(
            [arrows, ore],
            equipped: new() { ["ammunition"] = Stack("ore", 1) },
            inventorySlots: 0,
            bag: [BagSlot(PlayerBag, 0, BagSlotKindIds.General), BagSlot(PlayerBag, 1, BagSlotKindIds.Specialized, "ammo")]);

        var plan = Plan(request);

        Assert.Collection(
            plan.Steps,
            step => AssertPlace(step, arrows, 10, PlayerBag, 1),
            step => AssertPlace(step, ore, 1, PlayerBag, 0));
    }

    [Fact]
    public void ACorpseBagWithContentsTakesAnEmptyBagSlotWhole()
    {
        var corpseBagContents = Guid.NewGuid();
        var ore = CorpseItem("ore", 3, CorpseSectionKindIds.Bag);
        var pack = CorpseItem(
            "pack",
            1,
            CorpseSectionKindIds.Equipment,
            new LootAllBag(
                500,
                [
                    BagSlot(corpseBagContents, 0, BagSlotKindIds.General, occupant: OccupantOf(ore)),
                    BagSlot(corpseBagContents, 1, BagSlotKindIds.General)
                ]));
        var sword = CorpseItem("sword", 1, CorpseSectionKindIds.GeneralInventory);

        var plan = Plan(Request(
            [ore, pack, sword],
            equipped: new() { ["main_hand"] = Stack("sword", 1) },
            inventorySlots: 0));

        Assert.Collection(
            plan.Steps,
            step => AssertStep(step, LootAllStepKind.EquipBag, pack, 1, equipmentSlotId: "bag"),
            step => AssertPlace(step, sword, 1, corpseBagContents, 1));
        Assert.Equal(1500, plan.CarryCapacity);
        Assert.Equal(80, plan.CarriedWeight);
    }

    [Fact]
    public void WithABagEquippedTheCorpseBagContentsComeFirstAndTheEmptiedBagFollows()
    {
        var corpseBagContents = Guid.NewGuid();
        var ore = CorpseItem("ore", 3, CorpseSectionKindIds.Bag);
        var pack = CorpseItem(
            "pack",
            1,
            CorpseSectionKindIds.Equipment,
            new LootAllBag(500, [BagSlot(corpseBagContents, 0, BagSlotKindIds.General, occupant: OccupantOf(ore))]));

        var plan = Plan(Request([pack, ore], equipped: new() { ["bag"] = Stack("pack", 1) }));

        Assert.Collection(
            plan.Steps,
            step => AssertPlace(step, ore, 3, Inventory, 0),
            step => AssertPlace(step, pack, 1, Inventory, 1));
    }

    [Fact]
    public void ACorpseBagStaysWhenAnyOfItsContentsStays()
    {
        var corpseBagContents = Guid.NewGuid();
        var ore = CorpseItem("ore", 3, CorpseSectionKindIds.Bag);
        var pack = CorpseItem(
            "pack",
            1,
            CorpseSectionKindIds.Equipment,
            new LootAllBag(500, [BagSlot(corpseBagContents, 0, BagSlotKindIds.General, occupant: OccupantOf(ore))]));

        var plan = Plan(Request([pack, ore], equipped: new() { ["bag"] = Stack("pack", 1) }, inventorySlots: 0));

        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void AnItemThatCannotChangeItsOwnerStays()
    {
        var sword = new LootAllCorpseItem(Guid.NewGuid(), "sword", 1, CorpseSectionKindIds.Equipment, Key, false);

        Assert.Empty(Plan(Request([sword])).Steps);
    }

    private static LootAllPlan Plan(LootAllRequest request)
    {
        return CorpseLootAllPlanner.Plan(request);
    }

    private static LootAllRequest Request(
        IReadOnlyList<LootAllCorpseItem> items,
        Dictionary<string, LootAllStack>? equipped = null,
        IReadOnlyList<LootAllStack>? inventory = null,
        int inventorySlots = 4,
        IReadOnlyList<LootAllCarriedSlot>? bag = null,
        long weight = 0,
        long capacity = 1000,
        bool wholeItemsOnly = false)
    {
        var slots = Enumerable.Range(0, Math.Max(inventorySlots, inventory?.Count ?? 0))
            .Select(index => new LootAllCarriedSlot(
                Inventory,
                ItemContainerTypeIds.PermanentInventory,
                index,
                BagSlotKindIds.General,
                [],
                inventory is not null && index < inventory.Count ? inventory[index] : null))
            .ToArray();
        return new LootAllRequest(
            id => Definitions.TryGetValue(id, out var definition) ? definition : null!,
            definition => definition.RequiredCharacterLevel <= 1,
            weight,
            capacity,
            SlotEntries.Select(slot => new LootAllEquipmentSlot(
                slot,
                equipped is not null && equipped.TryGetValue(slot.Id, out var occupant) ? occupant : null)),
            slots,
            bag ?? [],
            items,
            wholeItemsOnly);
    }

    private static void AssertStep(
        LootAllStep step,
        LootAllStepKind kind,
        LootAllCorpseItem item,
        int quantity,
        bool takesRest = true,
        string equipmentSlotId = "",
        Guid target = default)
    {
        Assert.Equal(kind, step.Kind);
        Assert.Same(item, step.Item);
        Assert.Equal(quantity, step.Quantity);
        Assert.Equal(takesRest, step.TakesRest);
        if (equipmentSlotId.Length > 0)
        {
            Assert.Equal(equipmentSlotId, step.EquipmentSlotId);
        }

        if (target != default)
        {
            Assert.Equal(target, step.TargetItemInstanceId);
        }
    }

    private static void AssertPlace(
        LootAllStep step,
        LootAllCorpseItem item,
        int quantity,
        Guid containerId,
        int slotIndex,
        bool takesRest = true)
    {
        AssertStep(step, LootAllStepKind.Place, item, quantity, takesRest);
        Assert.Equal(containerId, step.Destination.ContainerId);
        Assert.Equal(slotIndex, step.Destination.SlotIndex);
    }

    private static LootAllCorpseItem CorpseItem(string definitionId, int quantity, string section, LootAllBag? bag = null)
    {
        return new LootAllCorpseItem(Guid.NewGuid(), definitionId, quantity, section, Key, true, bag);
    }

    private static LootAllStack OccupantOf(LootAllCorpseItem item)
    {
        return new LootAllStack(item.ItemInstanceId, item.DefinitionId, item.Quantity, item.MergeKey);
    }

    private static LootAllStack Stack(string definitionId, int quantity)
    {
        return new LootAllStack(Guid.NewGuid(), definitionId, quantity, Key);
    }

    private static LootAllCarriedSlot BagSlot(
        Guid containerId,
        int index,
        string kind,
        string? tag = null,
        LootAllStack? occupant = null)
    {
        return new LootAllCarriedSlot(
            containerId,
            ItemContainerTypeIds.BagContents,
            index,
            kind,
            tag is null ? [] : [tag],
            occupant);
    }

    private static ItemEquipmentSlotEntry Slot(string id, int maximumQuantity = 1)
    {
        return new ItemEquipmentSlotEntry { Id = id, DisplayName = id, MaximumQuantity = maximumQuantity };
    }

    private static ItemDefinition Definition(
        string id,
        long unitWeight,
        int maximumStackSize,
        string[] equipmentSlots,
        string[]? blocks = null,
        string[]? tags = null,
        int level = 1)
    {
        return new ItemDefinition
        {
            Id = id,
            DisplayName = id,
            Category = "material",
            Tags = tags ?? [],
            ToolKinds = [],
            UnitWeight = unitWeight,
            MaximumStackSize = maximumStackSize,
            EquipmentSlots = equipmentSlots,
            BlockedEquipmentSlots = blocks ?? [],
            RequiredCharacterLevel = level,
            StructuralFingerprint = id
        };
    }
}
