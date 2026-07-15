using ShooterMmo.Tools.ItemCatalogCompiler;
using ShooterMmo.WorldData.Items;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class ItemCatalogTests
{
    private static readonly string[] RepresentativeDefinitionIds =
    [
        "ammunition.training_556",
        "armor.starter_vest",
        "bag.field_pack",
        "material.iron_ore",
        "medical.field_dressing",
        "quest_item.signal_transponder",
        "ring.starter_band",
        "tool.starter_pickaxe",
        "weapon.training_rifle"
    ];

    [Fact]
    public void CheckedInCatalogContainsEveryRepresentativeDefinitionAndMatchesRuntime()
    {
        var catalog = ItemCatalogTestData.Compile();

        Assert.Equal(ItemCatalogFormat.Version, catalog.FormatVersion);
        Assert.Equal("core", catalog.CatalogId);
        Assert.Equal(64, catalog.Revision.Length);
        Assert.Equal(
            RepresentativeDefinitionIds,
            catalog.Definitions.Select(definition => definition.Id));
        Assert.Equal(1, Definition(catalog, "ammunition.training_556").UnitWeight);
        Assert.Equal(25, Definition(catalog, "weapon.training_rifle").UnitWeight);
        Assert.Equal(50, Definition(catalog, "bag.field_pack").Bag.CarryCapacityBonus);
        Assert.Equal(4, catalog.SecureContainerTiers.Single().SlotCapacity);
        Assert.All(catalog.Definitions, definition =>
            Assert.Equal(64, definition.StructuralFingerprint.Length));
        Assert.Equal(
            ItemCatalogJson.SerializeRuntime(catalog),
            NormalizeLineEndings(ItemCatalogTestData.RuntimeJson));
    }

    [Fact]
    public void CatalogRevisionIsIndependentOfAuthoringOrder()
    {
        var firstAuthoring = ItemCatalogTestData.LoadAuthoring();
        var reorderedAuthoring = ItemCatalogTestData.LoadAuthoring();
        Array.Reverse(reorderedAuthoring.Categories);
        Array.Reverse(reorderedAuthoring.Tags);
        Array.Reverse(reorderedAuthoring.EquipmentSlots);
        Array.Reverse(reorderedAuthoring.Definitions);
        Array.Reverse(reorderedAuthoring.SecureContainerTiers);
        foreach (var definition in reorderedAuthoring.Definitions)
        {
            Array.Reverse(definition.Tags);
            Array.Reverse(definition.EquipmentSlots);
            Array.Reverse(definition.DefaultPolicies);
            if (definition.Bag is null)
            {
                continue;
            }

            Array.Reverse(definition.Bag.Slots);
            foreach (var slot in definition.Bag.Slots)
            {
                Array.Reverse(slot.AcceptedTags);
            }
        }

        var first = ItemCatalogCompiler.Compile(firstAuthoring);
        var reordered = ItemCatalogCompiler.Compile(reorderedAuthoring);

        Assert.Equal(first.Revision, reordered.Revision);
        Assert.Equal(
            first.Definitions.Select(definition => definition.StructuralFingerprint),
            reordered.Definitions.Select(definition => definition.StructuralFingerprint));
    }

    [Fact]
    public void DisplayOnlyChangePreservesStructuralFingerprint()
    {
        var authoring = ItemCatalogTestData.LoadAuthoring();
        var original = ItemCatalogCompiler.Compile(authoring);
        authoring.Definitions.Single(definition => definition.Id == "material.iron_ore")
            .DisplayName = "Iron Ore Sample";

        var changed = ItemCatalogCompiler.Compile(authoring);

        Assert.NotEqual(original.Revision, changed.Revision);
        Assert.Equal(
            Definition(original, "material.iron_ore").StructuralFingerprint,
            Definition(changed, "material.iron_ore").StructuralFingerprint);
    }

    [Fact]
    public void StructuralChangeChangesDefinitionFingerprint()
    {
        var authoring = ItemCatalogTestData.LoadAuthoring();
        var original = ItemCatalogCompiler.Compile(authoring);
        authoring.Definitions.Single(definition => definition.Id == "material.iron_ore")
            .UnitWeight++;

        var changed = ItemCatalogCompiler.Compile(authoring);

        Assert.NotEqual(original.Revision, changed.Revision);
        Assert.NotEqual(
            Definition(original, "material.iron_ore").StructuralFingerprint,
            Definition(changed, "material.iron_ore").StructuralFingerprint);
    }

    [Fact]
    public void DuplicateDefinitionIdsFailValidation()
    {
        var authoring = ItemCatalogTestData.LoadAuthoring();
        authoring.Definitions[1].Id = authoring.Definitions[0].Id;

        var exception = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(authoring));

        Assert.Contains(exception.Errors, error => error.Contains("duplicate id", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateBagSlotIndicesFailValidation()
    {
        var authoring = ItemCatalogTestData.LoadAuthoring();
        var bag = authoring.Definitions.Single(definition => definition.Bag is not null).Bag;
        bag.Slots[1].Index = bag.Slots[0].Index;

        var exception = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(authoring));

        Assert.Contains(exception.Errors, error => error.Contains("duplicate index", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeWeightFailsValidation()
    {
        var authoring = ItemCatalogTestData.LoadAuthoring();
        authoring.Definitions[0].UnitWeight = -1;

        var exception = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(authoring));

        Assert.Contains(
            exception.Errors,
            error => error.Contains("unitWeight", StringComparison.Ordinal));
    }

    [Fact]
    public void NonPositiveStackLimitFailsValidation()
    {
        var authoring = ItemCatalogTestData.LoadAuthoring();
        authoring.Definitions[0].MaximumStackSize = 0;

        var exception = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(authoring));

        Assert.Contains(
            exception.Errors,
            error => error.Contains("maximumStackSize", StringComparison.Ordinal));
    }

    [Fact]
    public void WeaponSecureContainerEligibilityFailsValidation()
    {
        var authoring = ItemCatalogTestData.LoadAuthoring();
        authoring.Definitions.Single(definition => definition.Category == ItemCategoryIds.Weapon)
            .LocationEligibility.SecureContainer = true;

        var exception = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(authoring));

        Assert.Contains(
            exception.Errors,
            error => error.Contains("cannot allow a weapon", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownJsonMembersFailValidation()
    {
        var malformed = ItemCatalogTestData.AuthoringJson.Replace(
            "\"catalogId\": \"core\",",
            "\"catalogId\": \"core\",\n  \"unknownCatalogField\": true,",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => ItemCatalogJson.DeserializeAuthoring(malformed));
    }

    [Fact]
    public void DecimalWeightFailsJsonValidation()
    {
        var malformed = ItemCatalogTestData.AuthoringJson.Replace(
            "\"unitWeight\": 1",
            "\"unitWeight\": 1.5",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => ItemCatalogJson.DeserializeAuthoring(malformed));
    }

    [Fact]
    public void MissingRequiredBooleanFailsValidation()
    {
        var malformed = ItemCatalogTestData.AuthoringJson.Replace(
            "      \"playerDestroyable\": true,",
            string.Empty,
            StringComparison.Ordinal);
        var authoring = ItemCatalogJson.DeserializeAuthoring(malformed);

        var exception = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(authoring));

        Assert.Contains(
            exception.Errors,
            error => error.Contains("playerDestroyable is required", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateJsonPropertiesFailValidation()
    {
        var malformed = ItemCatalogTestData.AuthoringJson.Replace(
            "\"catalogId\": \"core\",",
            "\"catalogId\": \"core\",\n  \"catalogId\": \"core\",",
            StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(
            () => ItemCatalogJson.DeserializeAuthoring(malformed));

        Assert.Contains("duplicate property", exception.Message, StringComparison.Ordinal);
    }

    private static ItemDefinition Definition(ItemCatalogRuntimeDocument catalog, string id)
    {
        return catalog.Definitions.Single(definition => definition.Id == id);
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }
}
