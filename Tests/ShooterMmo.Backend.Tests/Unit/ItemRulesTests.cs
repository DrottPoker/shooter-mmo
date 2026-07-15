using ShooterMmo.WorldData.Items;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class ItemRulesTests
{
    [Fact]
    public void StackCompatibilityRequiresMatchingDefinitionPolicyAndStackState()
    {
        var definition = Definition("material.iron_ore");
        var first = new ItemStackState(definition.Id, 40, "policy.none", "state.default");
        var second = new ItemStackState(definition.Id, 60, "policy.none", "state.default");
        var protectedStack = new ItemStackState(
            definition.Id,
            10,
            "policy.protected",
            "state.default");

        Assert.True(ItemStackRules.AreCompatible(first, second));
        Assert.True(ItemStackRules.CanMerge(definition, first, second));
        Assert.False(ItemStackRules.AreCompatible(first, protectedStack));
        Assert.False(ItemStackRules.CanMerge(
            definition,
            first,
            new ItemStackState(definition.Id, 61, "policy.none", "state.default")));
    }

    [Fact]
    public void SpecializedSlotsAcceptConfiguredTagsAndRejectUnrelatedDefinitions()
    {
        var bag = Definition("bag.field_pack").Bag;
        var medicalSlot = bag.Slots.Single(slot => slot.AcceptedTags.Contains(ItemTagIds.Medical));
        var generalSlot = bag.Slots.Single(slot => slot.Index == 0);
        var medical = Definition("medical.field_dressing");
        var material = Definition("material.iron_ore");
        var weapon = Definition("weapon.training_rifle");

        Assert.True(ItemSlotRules.Accepts(medical, medicalSlot));
        Assert.False(ItemSlotRules.Accepts(material, medicalSlot));
        Assert.False(ItemSlotRules.Accepts(weapon, medicalSlot));
        Assert.True(ItemSlotRules.Accepts(weapon, generalSlot));
    }

    [Fact]
    public void EquipmentCompatibilityUsesOnlyExplicitEquipmentSlots()
    {
        var weapon = Definition("weapon.training_rifle");
        var ring = Definition("ring.starter_band");

        Assert.True(ItemEquipmentRules.IsCompatible(weapon, ItemEquipmentSlotIds.PrimaryWeapon));
        Assert.True(ItemEquipmentRules.IsCompatible(weapon, ItemEquipmentSlotIds.SecondaryWeapon));
        Assert.False(ItemEquipmentRules.IsCompatible(weapon, ItemEquipmentSlotIds.Tool));
        Assert.True(ItemEquipmentRules.IsCompatible(ring, ItemEquipmentSlotIds.Ring1));
        Assert.True(ItemEquipmentRules.IsCompatible(ring, ItemEquipmentSlotIds.Ring2));
    }

    [Fact]
    public void WeaponsAreRejectedFromSecureContainerEvenIfContentIsMalformed()
    {
        var weapon = Definition("weapon.training_rifle");
        var malformedWeapon = new ItemDefinition
        {
            Id = weapon.Id,
            Category = ItemCategoryIds.Weapon,
            LocationEligibility = new ItemLocationEligibility
            {
                SecureContainer = true
            }
        };

        Assert.False(SecureContainerRules.IsEligible(weapon));
        Assert.False(SecureContainerRules.IsEligible(malformedWeapon));
        Assert.True(SecureContainerRules.IsEligible(Definition("medical.field_dressing")));
    }

    [Fact]
    public void EmptyBagCanUseGeneralSlotsAndNonEmptyBagCannot()
    {
        Assert.True(BagLocationRules.CanPlace(
            false,
            BagDestinationKind.PermanentInventoryGeneralSlot,
            false));
        Assert.True(BagLocationRules.CanPlace(
            false,
            BagDestinationKind.BagGeneralSlot,
            false));
        Assert.False(BagLocationRules.CanPlace(
            true,
            BagDestinationKind.PermanentInventoryGeneralSlot,
            false));
        Assert.False(BagLocationRules.CanPlace(
            true,
            BagDestinationKind.BagGeneralSlot,
            false));
        Assert.True(BagLocationRules.CanPlace(
            true,
            BagDestinationKind.CharacterBagEquipmentSlot,
            false));
    }

    [Fact]
    public void BagPlacementRejectsSpecializedSlotsAndContainmentCycles()
    {
        Assert.False(BagLocationRules.CanPlace(
            false,
            BagDestinationKind.BagSpecializedSlot,
            false));
        Assert.False(BagLocationRules.CanPlace(
            false,
            BagDestinationKind.BagGeneralSlot,
            true));
        Assert.False(BagLocationRules.CanPlace(
            true,
            BagDestinationKind.CharacterBagEquipmentSlot,
            true));
    }

    [Fact]
    public void NeutralIntegerWeightArithmeticUsesFullStackQuantity()
    {
        Assert.Equal(60, ItemWeightRules.CalculateStackWeight(1, 60));
        Assert.Equal(85, ItemWeightRules.SumWeight([10, 60, 15]));
        Assert.Throws<OverflowException>(() =>
            ItemWeightRules.CalculateStackWeight(long.MaxValue, 2));
    }

    [Theory]
    [InlineData(200, 10000)]
    [InlineData(210, 9000)]
    [InlineData(220, 8000)]
    [InlineData(240, 6000)]
    [InlineData(260, 4000)]
    [InlineData(280, 2000)]
    public void EncumbranceReferencePointsAreExact(
        long carriedWeight,
        int expectedBasisPoints)
    {
        Assert.Equal(
            expectedBasisPoints,
            EncumbranceRules.CalculateMovementMultiplierBasisPoints(
                carriedWeight,
                CarryWeightDefaults.BaseCharacterCapacity));
    }

    [Fact]
    public void BaseCharacterCapacityUsesTheCanonicalNeutralWeightScale()
    {
        Assert.Equal(200, CarryWeightDefaults.BaseCharacterCapacity);
        Assert.True(EncumbranceRules.IsWithinHardCap(
            280,
            CarryWeightDefaults.BaseCharacterCapacity));
        Assert.False(EncumbranceRules.IsWithinHardCap(
            281,
            CarryWeightDefaults.BaseCharacterCapacity));
    }

    [Fact]
    public void HardCapComparisonRejectsTheFirstIntegerAboveOneHundredFortyPercent()
    {
        const long capacity = 1_000_000_003;
        const long maximumAllowedWeight = 1_400_000_004;

        Assert.True(EncumbranceRules.IsWithinHardCap(maximumAllowedWeight, capacity));
        Assert.False(EncumbranceRules.IsWithinHardCap(maximumAllowedWeight + 1, capacity));
        Assert.True(EncumbranceRules.IsWithinHardCap(280, 200));
        Assert.False(EncumbranceRules.IsWithinHardCap(281, 200));
    }

    private static ItemDefinition Definition(string id)
    {
        return ItemCatalogTestData.Compile().Definitions.Single(definition => definition.Id == id);
    }
}
