using Mmorpg.Testing.Content;
using Mmorpg.Tools.ItemCatalogCompiler;
using Mmorpg.WorldData.Combat;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Shared.Tests;

public sealed class ItemCatalogTests
{
    private static readonly string[] RepresentativeDefinitionIds =
    [
        "ammunition.bronze_arrow",
        "armor.heater_shield",
        "armor.leather_boots",
        "armor.leather_gloves",
        "armor.leather_jerkin",
        "armor.nasal_helm",
        "bag.travelers_pack",
        "clothing.blue_wool_cloak",
        "clothing.blue_wool_hood",
        "clothing.blue_wool_robe",
        "clothing.linen_shirt",
        "clothing.long_linen_shirt",
        "clothing.net_shirt",
        "clothing.red_wool_cloak",
        "clothing.red_wool_hood",
        "clothing.red_wool_robe",
        "clothing.wool_cloak",
        "clothing.wool_hood",
        "clothing.wool_robe",
        "clothing.wool_trousers",
        "food.cooked_trout",
        "jewelry.copper_ring",
        "material.iron_ore",
        "material.oak_log",
        "material.wolf_pelt",
        "potion.minor_healing",
        "quest_item.sealed_letter",
        "tool.starter_pickaxe",
        "tool.woodcutting_axe",
        "weapon.iron_greataxe",
        "weapon.iron_sword",
        "weapon.oak_staff",
        "weapon.training_shortbow"
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
        Assert.Equal(1, Definition(catalog, "ammunition.bronze_arrow").UnitWeight);
        Assert.Equal(80, Definition(catalog, "weapon.training_shortbow").UnitWeight);
        Assert.Equal(500, Definition(catalog, "bag.travelers_pack").Bag.CarryCapacityBonus);
        Assert.Equal(2000, catalog.Character.BaseCarryCapacity);
        Assert.Equal(1000, catalog.EquipmentSlots.Single(slot => slot.Id == "ammunition").MaximumQuantity);
        Assert.Equal(4, catalog.SecureContainerTiers.Single().SlotCapacity);
        Assert.Equal(200, catalog.Progression.MaximumCharacterLevel);
        Assert.Equal(4, catalog.WeaponFamilies.Length);
        var bow = catalog.WeaponFamilies.Single(family => family.Id == "bow");
        Assert.Equal(
            ("bow", CombatStyleIds.Ranged, "arrow", 30f, 0.8f),
            (bow.Id, bow.CombatStyle, bow.AmmunitionFamily, bow.ProjectileSpeed, bow.WindupSeconds));
        // A melee family fires nothing and hits at once when its windup ends.
        var sword = catalog.WeaponFamilies.Single(family => family.Id == "sword");
        Assert.Equal(
            (CombatStyleIds.Melee, string.Empty, 0f, 0.35f),
            (sword.CombatStyle, sword.AmmunitionFamily, sword.ProjectileSpeed, sword.WindupSeconds));
        var axe = catalog.WeaponFamilies.Single(family => family.Id == "axe");
        Assert.Equal(
            (CombatStyleIds.Melee, string.Empty, 0f, 0.6f),
            (axe.CombatStyle, axe.AmmunitionFamily, axe.ProjectileSpeed, axe.WindupSeconds));
        var greataxe = Definition(catalog, "weapon.iron_greataxe");
        Assert.Equal(
            ("axe", DamageTypeIds.Normal, 16, 27, 3.0f, 2.5f),
            (greataxe.Weapon.WeaponFamily, greataxe.Weapon.DamageType, greataxe.Weapon.MinimumDamage,
                greataxe.Weapon.MaximumDamage, greataxe.Weapon.AttackIntervalSeconds, greataxe.Weapon.Range));
        Assert.Equal((180, 180L), (greataxe.UnitWeight, greataxe.Value));
        Assert.Equal(new[] { "off_hand" }, greataxe.BlockedEquipmentSlots);
        var shortbow = Definition(catalog, "weapon.training_shortbow").Weapon;
        Assert.Equal(
            ("bow", DamageTypeIds.Normal, 10, 18),
            (shortbow.WeaponFamily, shortbow.DamageType, shortbow.MinimumDamage, shortbow.MaximumDamage));
        Assert.Equal((2.4f, 10f), (shortbow.AttackIntervalSeconds, shortbow.Range));
        var ironSword = Definition(catalog, "weapon.iron_sword").Weapon;
        Assert.Equal(
            ("sword", DamageTypeIds.Normal, 11, 19),
            (ironSword.WeaponFamily, ironSword.DamageType, ironSword.MinimumDamage, ironSword.MaximumDamage));
        Assert.Equal((2.4f, 2.5f), (ironSword.AttackIntervalSeconds, ironSword.Range));
        // A magic family fires a bolt without ammunition, and only its weapons cost mana.
        var staffFamily = catalog.WeaponFamilies.Single(family => family.Id == "staff");
        Assert.Equal(
            (CombatStyleIds.Magic, string.Empty, 20f, 0.15f),
            (staffFamily.CombatStyle, staffFamily.AmmunitionFamily, staffFamily.ProjectileSpeed, staffFamily.WindupSeconds));
        var oakStaff = Definition(catalog, "weapon.oak_staff");
        Assert.Equal(
            ("staff", DamageTypeIds.Normal, 10, 18, 5),
            (oakStaff.Weapon.WeaponFamily, oakStaff.Weapon.DamageType, oakStaff.Weapon.MinimumDamage,
                oakStaff.Weapon.MaximumDamage, oakStaff.Weapon.ManaCost));
        Assert.Equal((2.4f, 10f), (oakStaff.Weapon.AttackIntervalSeconds, oakStaff.Weapon.Range));
        Assert.Equal(new[] { "off_hand" }, oakStaff.BlockedEquipmentSlots);
        Assert.Equal((0, 0), (shortbow.ManaCost, ironSword.ManaCost));
        var arrow = Definition(catalog, "ammunition.bronze_arrow").Ammunition;
        Assert.Equal(("arrow", 5), (arrow.AmmunitionFamily, arrow.DamageBonusPercent));
        Assert.All(catalog.Definitions, definition =>
        {
            Assert.Equal(64, definition.StructuralFingerprint.Length);
            Assert.Equal(1, definition.RequiredCharacterLevel);
        });
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
        Array.Reverse(reorderedAuthoring.Definitions);
        Array.Reverse(reorderedAuthoring.SecureContainerTiers);
        Array.Reverse(reorderedAuthoring.Retired.EquipmentSlots);
        foreach (var definition in reorderedAuthoring.Definitions)
        {
            Array.Reverse(definition.Tags);
            Array.Reverse(definition.EquipmentSlots);
            Array.Reverse(definition.BlockedEquipmentSlots);
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

    // Equipment slots are listed in display order, so their order is part of the revision
    // but never of a structural fingerprint.
    [Fact]
    public void EquipmentSlotDisplayOrderChangesOnlyTheRevision()
    {
        var authoring = ItemCatalogTestData.LoadAuthoring();
        var original = ItemCatalogCompiler.Compile(authoring);
        Array.Reverse(authoring.EquipmentSlots);

        var reordered = ItemCatalogCompiler.Compile(authoring);

        Assert.NotEqual(original.Revision, reordered.Revision);
        Assert.Equal(
            original.EquipmentSlots.Select(slot => slot.Id).Reverse(),
            reordered.EquipmentSlots.Select(slot => slot.Id));
        Assert.Equal(
            original.Definitions.Select(definition => definition.StructuralFingerprint),
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

    // Pinned when catalog format 4 added combat data: a format change that adds no structural
    // field must keep every persisted definition compatible.
    [Fact]
    public void StructuralFingerprintsDoNotDependOnTheCatalogFormatVersion()
    {
        var catalog = ItemCatalogTestData.Compile();

        Assert.Equal(3, ItemCatalogFormat.StructuralFingerprintVersion);
        Assert.Equal(
            "f062a6af3f7a41e821539b89a50408d07abd5a6577548fe54e7167c749542732",
            Definition(catalog, "weapon.training_shortbow").StructuralFingerprint);
    }

    [Fact]
    public void CombatDataAndLevelRequirementsChangeOnlyTheRevision()
    {
        var original = ItemCatalogTestData.Compile();
        var authoring = ItemCatalogTestData.LoadAuthoring();
        var shortbow = AuthoringDefinition(authoring, "weapon.training_shortbow");
        shortbow.Weapon.MaximumDamage++;
        shortbow.RequiredCharacterLevel = 5;
        authoring.WeaponFamilies.Single(family => family.Id == "bow").ProjectileSpeed = 40f;

        var changed = ItemCatalogCompiler.Compile(authoring);

        Assert.NotEqual(original.Revision, changed.Revision);
        Assert.Equal(
            original.Definitions.Select(definition => definition.StructuralFingerprint),
            changed.Definitions.Select(definition => definition.StructuralFingerprint));
    }

    [Theory]
    [InlineData("missing-weapon", "uses category 'weapon' but has no weapon block")]
    [InlineData("weapon-on-material", "has a weapon block but does not use category 'weapon'")]
    [InlineData("unknown-weapon-family", "references unknown weapon family")]
    [InlineData("unknown-damage-type", ".weapon.damageType must be")]
    [InlineData("inverted-damage", ".weapon.minimumDamage and maximumDamage")]
    [InlineData("zero-damage", ".weapon.minimumDamage and maximumDamage")]
    [InlineData("short-interval", ".weapon.attackIntervalSeconds")]
    [InlineData("nan-range", ".weapon.range")]
    [InlineData("missing-ammunition", "uses category 'ammunition' but has no ammunition block")]
    [InlineData("unknown-ammunition-family", "references unknown ammunition family")]
    [InlineData("negative-bonus", ".ammunition.damageBonusPercent")]
    [InlineData("level-zero", ".requiredCharacterLevel must be between 1")]
    [InlineData("level-above-cap", ".requiredCharacterLevel must be between 1")]
    [InlineData("missing-progression", "progression is required")]
    [InlineData("ranged-without-ammunition", "weaponFamilies[bow].ammunitionFamily references unknown ammunition family")]
    [InlineData("ranged-without-speed", "projectileSpeed must be positive for a ranged family")]
    [InlineData("melee-with-ammunition", "ammunitionFamily must be empty unless the style is ranged")]
    [InlineData("melee-with-speed", "projectileSpeed must be 0 for a melee family")]
    [InlineData("unknown-style", "combatStyle must be melee, ranged, or magic")]
    [InlineData("duplicate-weapon-family", "weaponFamilies contains duplicate id 'bow'")]
    [InlineData("missing-weapon-families", "weaponFamilies is required")]
    [InlineData("magic-without-mana-cost", ".weapon.manaCost must be between 1")]
    [InlineData("magic-mana-cost-too-high", ".weapon.manaCost must be between 1")]
    [InlineData("ranged-with-mana-cost", ".weapon.manaCost must be 0 unless the weapon family is magic")]
    [InlineData("missing-mana-cost", ".weapon.manaCost must be 0 unless the weapon family is magic")]
    [InlineData("missing-windup", "weaponFamilies[bow].windupSeconds must be between 0 and 5")]
    [InlineData("negative-windup", "weaponFamilies[bow].windupSeconds must be between 0 and 5")]
    [InlineData("windup-too-long", "weaponFamilies[bow].windupSeconds must be between 0 and 5")]
    [InlineData("interval-within-windup", ".weapon.attackIntervalSeconds must be longer than the windup of its weapon family, 0.8 s")]
    public void InvalidCombatDataFailsValidation(string mutation, string expectedError)
    {
        var authoring = ItemCatalogTestData.LoadAuthoring();
        var shortbow = AuthoringDefinition(authoring, "weapon.training_shortbow");
        var arrow = AuthoringDefinition(authoring, "ammunition.bronze_arrow");
        var ore = AuthoringDefinition(authoring, "material.iron_ore");
        var bow = authoring.WeaponFamilies.Single(family => family.Id == "bow");
        var staff = AuthoringDefinition(authoring, "weapon.oak_staff");
        // The bow's place among the families depends on the content, not on the rule.
        expectedError = expectedError.Replace(
            "[bow]",
            "[" + Array.IndexOf(authoring.WeaponFamilies, bow) + "]",
            StringComparison.Ordinal);
        switch (mutation)
        {
            case "missing-windup":
                bow.WindupSeconds = -1f;
                break;
            case "negative-windup":
                bow.WindupSeconds = -0.1f;
                break;
            case "windup-too-long":
                bow.WindupSeconds = CombatContentRules.MaximumWindupSeconds + 0.1f;
                break;
            case "interval-within-windup":
                shortbow.Weapon.AttackIntervalSeconds = 0.8f;
                break;
            case "magic-without-mana-cost":
                staff.Weapon.ManaCost = 0;
                break;
            case "magic-mana-cost-too-high":
                staff.Weapon.ManaCost = CombatContentRules.MaximumMana + 1;
                break;
            case "ranged-with-mana-cost":
                shortbow.Weapon.ManaCost = 5;
                break;
            case "missing-mana-cost":
                shortbow.Weapon.ManaCost = -1;
                break;
            case "missing-weapon":
                shortbow.Weapon = null;
                break;
            case "weapon-on-material":
                ore.Weapon = shortbow.Weapon;
                break;
            case "unknown-weapon-family":
                shortbow.Weapon.WeaponFamily = "crossbow";
                break;
            case "unknown-damage-type":
                shortbow.Weapon.DamageType = "lightning";
                break;
            case "inverted-damage":
                shortbow.Weapon.MinimumDamage = shortbow.Weapon.MaximumDamage + 1;
                break;
            case "zero-damage":
                shortbow.Weapon.MinimumDamage = 0;
                shortbow.Weapon.MaximumDamage = 0;
                break;
            case "short-interval":
                shortbow.Weapon.AttackIntervalSeconds = 0.1f;
                break;
            case "nan-range":
                shortbow.Weapon.Range = float.NaN;
                break;
            case "missing-ammunition":
                arrow.Ammunition = null;
                break;
            case "unknown-ammunition-family":
                arrow.Ammunition.AmmunitionFamily = "bolt";
                break;
            case "negative-bonus":
                arrow.Ammunition.DamageBonusPercent = -1;
                break;
            case "level-zero":
                ore.RequiredCharacterLevel = 0;
                break;
            case "level-above-cap":
                shortbow.RequiredCharacterLevel = authoring.Progression.MaximumCharacterLevel + 1;
                break;
            case "missing-progression":
                authoring.Progression = null;
                break;
            case "ranged-without-ammunition":
                bow.AmmunitionFamily = "bolt";
                break;
            case "ranged-without-speed":
                bow.ProjectileSpeed = 0f;
                break;
            case "melee-with-ammunition":
                bow.CombatStyle = CombatStyleIds.Melee;
                bow.ProjectileSpeed = 0f;
                break;
            case "melee-with-speed":
                bow.CombatStyle = CombatStyleIds.Melee;
                bow.AmmunitionFamily = string.Empty;
                break;
            case "unknown-style":
                bow.CombatStyle = "siege";
                break;
            case "duplicate-weapon-family":
                authoring.WeaponFamilies = [bow, bow];
                break;
            case "missing-weapon-families":
                authoring.WeaponFamilies = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var exception = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(authoring));

        Assert.Contains(
            exception.Errors,
            error => error.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDamageValueFailsValidation()
    {
        const string definitionId = "weapon.training_shortbow";
        var malformed = ItemCatalogTestData.DefinitionJson(definitionId).Replace(
            "    \"minimumDamage\": 10,\n",
            string.Empty,
            StringComparison.Ordinal);
        var authoring = ItemCatalogTestData.LoadAuthoring();
        var index = Array.FindIndex(authoring.Definitions, value => value.Id == definitionId);
        authoring.Definitions[index] = ItemCatalogJson.DeserializeDefinition(malformed);

        var exception = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(authoring));

        Assert.Contains(
            exception.Errors,
            error => error.Contains(".weapon.minimumDamage", StringComparison.Ordinal));
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
        authoring.Definitions.First(definition => definition.Category == ItemCategoryIds.Weapon)
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
        var malformed = ItemCatalogTestData.CatalogJson.Replace(
            "\"catalogId\": \"core\",",
            "\"catalogId\": \"core\",\n  \"unknownCatalogField\": true,",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => ItemCatalogJson.DeserializeCatalog(malformed));
    }

    [Fact]
    public void CatalogFileRejectsInlineDefinitions()
    {
        var malformed = ItemCatalogTestData.CatalogJson.Replace(
            "\"catalogId\": \"core\",",
            "\"catalogId\": \"core\",\n  \"definitions\": [],",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => ItemCatalogJson.DeserializeCatalog(malformed));
    }

    [Fact]
    public void DecimalWeightFailsJsonValidation()
    {
        var malformed = ItemCatalogTestData.DefinitionJson("ammunition.bronze_arrow").Replace(
            "\"unitWeight\": 1",
            "\"unitWeight\": 1.5",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(
            () => ItemCatalogJson.DeserializeDefinition(malformed));
    }

    [Fact]
    public void MissingRequiredBooleanFailsValidation()
    {
        const string definitionId = "ammunition.bronze_arrow";
        var malformed = ItemCatalogTestData.DefinitionJson(definitionId).Replace(
            "  \"playerDestroyable\": true,\n",
            string.Empty,
            StringComparison.Ordinal);
        var authoring = ItemCatalogTestData.LoadAuthoring();
        var index = Array.FindIndex(authoring.Definitions, value => value.Id == definitionId);
        authoring.Definitions[index] = ItemCatalogJson.DeserializeDefinition(malformed);

        var exception = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(authoring));

        Assert.Contains(
            exception.Errors,
            error => error.Contains("playerDestroyable is required", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateJsonPropertiesFailValidation()
    {
        var malformed = ItemCatalogTestData.CatalogJson.Replace(
            "\"catalogId\": \"core\",",
            "\"catalogId\": \"core\",\n  \"catalogId\": \"core\",",
            StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(
            () => ItemCatalogJson.DeserializeCatalog(malformed));

        Assert.Contains("duplicate property", exception.Message, StringComparison.Ordinal);
    }

    private static ItemDefinitionAuthoringEntry AuthoringDefinition(
        ItemCatalogAuthoringDocument authoring,
        string id)
    {
        return authoring.Definitions.Single(definition => definition.Id == id);
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
