using Mmorpg.GameSimulation;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Shared.Tests;

public sealed class ItemRulesTests
{
    [Fact]
    public void StackCompatibilityRequiresMatchingDefinitionPolicyAndStackState()
    {
        var definition = Definition("fixture.ore");
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
        var bag = Definition("fixture.pack").Bag;
        var potionSlot = bag.Slots.Single(slot => slot.AcceptedTags.Contains("potion"));
        var generalSlot = bag.Slots.Single(slot => slot.Index == 0);
        var potion = Definition("fixture.potion");
        var material = Definition("fixture.ore");
        var weapon = Definition("fixture.sword");

        Assert.True(ItemSlotRules.Accepts(potion, potionSlot));
        Assert.False(ItemSlotRules.Accepts(material, potionSlot));
        Assert.False(ItemSlotRules.Accepts(weapon, potionSlot));
        Assert.True(ItemSlotRules.Accepts(weapon, generalSlot));
    }

    [Fact]
    public void EquipmentCompatibilityUsesOnlyExplicitEquipmentSlots()
    {
        var weapon = Definition("fixture.sword");
        var ring = Definition("fixture.ring");

        Assert.True(ItemEquipmentRules.IsCompatible(weapon, "main_hand"));
        Assert.True(ItemEquipmentRules.IsCompatible(weapon, "off_hand"));
        Assert.False(ItemEquipmentRules.IsCompatible(weapon, "tool"));
        Assert.True(ItemEquipmentRules.IsCompatible(ring, "ring_1"));
        Assert.True(ItemEquipmentRules.IsCompatible(ring, "ring_2"));
    }

    [Fact]
    public void WeaponsAreRejectedFromSecureContainerEvenIfContentIsMalformed()
    {
        var weapon = Definition("fixture.sword");
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
        Assert.True(SecureContainerRules.IsEligible(Definition("fixture.potion")));
    }

    [Theory]
    [InlineData(ItemPolicyIds.ProtectedOnDeath)]
    [InlineData(ItemPolicyIds.Insured)]
    public void ActiveProtectionPoliciesBlockTransferCapabilities(string policyKind)
    {
        var capabilities = ItemPolicyRules.Evaluate(
            Definition("fixture.sword"),
            [new ItemPolicyState(policyKind, "active", "test", "source-1")]);

        Assert.False(capabilities.CanChangeOwningCharacter);
        Assert.False(capabilities.CanTrade);
        Assert.False(capabilities.CanListOnAuction);
        Assert.False(capabilities.CanSellToVendor);
    }

    [Fact]
    public void RemovedInsuranceRestoresNormalCapabilities()
    {
        var capabilities = ItemPolicyRules.Evaluate(
            Definition("fixture.sword"),
            [new ItemPolicyState(ItemPolicyIds.Insured, "removed", "test", "source-1")]);

        Assert.True(capabilities.CanChangeOwningCharacter);
        Assert.True(capabilities.CanTrade);
        Assert.True(capabilities.CanListOnAuction);
        Assert.True(capabilities.CanSellToVendor);
        Assert.Equal(ItemDeathDisposition.Lootable, capabilities.DeathDisposition);
    }

    [Fact]
    public void ProtectedPolicyTakesDeathPriorityAndBlocksQuestItemDestruction()
    {
        var capabilities = ItemPolicyRules.Evaluate(
            Definition("fixture.letter"),
            [
                new ItemPolicyState(
                    ItemPolicyIds.Insured,
                    "active",
                    "insurance_test",
                    "insurance-1"),
                new ItemPolicyState(
                    ItemPolicyIds.ProtectedOnDeath,
                    "active",
                    "quest_test",
                    "quest-1")
            ]);

        Assert.False(capabilities.CanPlayerDestroy);
        Assert.Equal(ItemDeathDisposition.ProtectedRecovery, capabilities.DeathDisposition);
    }

    [Fact]
    public void InsuranceIsLimitedToNonStackableEquipmentDefinitions()
    {
        Assert.True(ItemPolicyRules.CanApplyInsurance(Definition("fixture.sword")));
        Assert.True(ItemPolicyRules.CanApplyInsurance(Definition("fixture.pack")));
        Assert.False(ItemPolicyRules.CanApplyInsurance(Definition("fixture.arrow")));
        Assert.False(ItemPolicyRules.CanApplyInsurance(Definition("fixture.letter")));
    }

    [Fact]
    public void StackingCapabilityKeepsProtectionSeparateFromExactLineageCompatibility()
    {
        var definition = Definition("fixture.ore");
        var protectedCapabilities = ItemPolicyRules.Evaluate(
            definition,
            [
                new ItemPolicyState(
                    ItemPolicyIds.ProtectedOnDeath,
                    "active",
                    "system_grant",
                    "lineage-1")
            ]);
        var malformedInsuredCapabilities = ItemPolicyRules.Evaluate(
            definition,
            [
                new ItemPolicyState(
                    ItemPolicyIds.Insured,
                    "active",
                    "insurance_test",
                    "insurance-1")
            ]);

        Assert.True(protectedCapabilities.CanStack);
        Assert.False(malformedInsuredCapabilities.CanStack);
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
            EncumbranceRules.CalculateMovementMultiplierBasisPoints(carriedWeight, 200));
        Assert.Equal(
            expectedBasisPoints,
            new PlayerCarryState(0, carriedWeight, 200).MovementMultiplierBasisPoints);
    }

    [Fact]
    public void HardCapIsOneHundredFortyPercentOfCapacity()
    {
        Assert.Equal(140, EncumbranceRules.HardCapLoadPercent);
        Assert.True(EncumbranceRules.IsWithinHardCap(280, 200));
        Assert.False(EncumbranceRules.IsWithinHardCap(281, 200));
        Assert.True(EncumbranceRules.IsWithinHardCap(2800, 2000));
        Assert.False(EncumbranceRules.IsWithinHardCap(2801, 2000));
    }

    [Fact]
    public void EquipmentSlotQuantityIsTheSmallerOfSlotAndStackLimits()
    {
        var catalog = ItemCatalogTestData.CompileFixture();
        var arrow = Definition("fixture.arrow");
        var ammunitionSlot = catalog.EquipmentSlots.Single(slot => slot.Id == "ammunition");
        var headSlot = catalog.EquipmentSlots.Single(slot => slot.Id == "head");

        Assert.Equal(50, ItemEquipmentRules.MaximumQuantity(arrow, ammunitionSlot));
        Assert.True(ItemEquipmentRules.CanHoldQuantity(arrow, ammunitionSlot, 50));
        Assert.False(ItemEquipmentRules.CanHoldQuantity(arrow, ammunitionSlot, 51));
        Assert.False(ItemEquipmentRules.CanHoldQuantity(arrow, ammunitionSlot, 0));
        Assert.Equal(1, ItemEquipmentRules.MaximumQuantity(Definition("fixture.tunic"), headSlot));
    }

    [Fact]
    public void TwoHandedWeaponAndOffHandItemBlockEachOther()
    {
        var bow = Definition("fixture.bow");
        var shield = Definition("fixture.shield");
        var sword = Definition("fixture.sword");
        var withShield = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["off_hand"] = shield.BlockedEquipmentSlots
        };
        var withBow = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["main_hand"] = bow.BlockedEquipmentSlots
        };

        Assert.Equal("off_hand", ItemEquipmentRules.FindBlockingSlot(bow, "main_hand", withShield));
        Assert.Equal("main_hand", ItemEquipmentRules.FindBlockingSlot(shield, "off_hand", withBow));
        Assert.Equal("main_hand", ItemEquipmentRules.FindBlockingSlot(sword, "off_hand", withBow));
        Assert.Null(ItemEquipmentRules.FindBlockingSlot(sword, "main_hand", withShield));
        Assert.Null(ItemEquipmentRules.FindBlockingSlot(
            bow,
            "main_hand",
            new Dictionary<string, string[]>(StringComparer.Ordinal)));
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

    [Theory]
    // Within the hard cap afterwards is always allowed.
    [InlineData(100, 200, 280, 200, true)]
    [InlineData(348, 200, 280, 200, true)]
    // A voluntary change may not cross the hard cap.
    [InlineData(280, 200, 281, 200, false)]
    // An existing overflow may shrink, but not grow or worsen its load ratio.
    [InlineData(348, 200, 340, 200, true)]
    [InlineData(348, 200, 348, 200, true)]
    [InlineData(348, 200, 349, 200, false)]
    [InlineData(348, 200, 300, 150, false)]
    [InlineData(348, 200, 340, 250, true)]
    public void VoluntaryTransitionsKeepOrShrinkAnInvoluntaryOverflow(
        long previousWeight,
        long previousCapacity,
        long nextWeight,
        long nextCapacity,
        bool expected)
    {
        Assert.Equal(
            expected,
            EncumbranceRules.IsAllowedVoluntaryTransition(
                previousWeight,
                previousCapacity,
                nextWeight,
                nextCapacity));
    }

    [Fact]
    public void MovementMultiplierStaysAtTheMinimumAboveTheHardCap()
    {
        Assert.Equal(
            EncumbranceRules.MinimumMovementMultiplierBasisPoints,
            EncumbranceRules.CalculateMovementMultiplierBasisPoints(348, 200));
    }

    private static ItemDefinition Definition(string id)
    {
        return ItemCatalogTestData.CompileFixture().Definitions.Single(definition => definition.Id == id);
    }
}
