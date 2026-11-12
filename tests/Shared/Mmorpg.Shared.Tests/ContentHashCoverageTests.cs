using Mmorpg.SharedTests;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Shared.Tests;

// Every serialized field of a compiled document is hashed by its Stamp, or listed here as
// unhashed with the reason, as Changing A Content Format in content/README.md describes.
public sealed class ContentHashCoverageTests
{
    private const string AuthoringText = "authoring-only text: the compiled document leaves it empty and the text table holds it";
    private const string ClientText = "client-filled text: empty on servers, a client fills it from the quest text table";

    [Fact]
    public void TheItemCatalogStampCoversEveryField()
    {
        var findings = HashCoverage.Check(
            HashCoverage.Sample<ItemCatalogRuntimeDocument>,
            StampItems,
            "ItemCatalogCompiler.Stamp",
            new Dictionary<string, string>
            {
                ["Categories[].DisplayName"] = AuthoringText,
                ["Tags[].DisplayName"] = AuthoringText,
                ["ToolKinds[].DisplayName"] = AuthoringText,
                ["EquipmentSlots[].DisplayName"] = AuthoringText,
                ["WeaponFamilies[].DisplayName"] = AuthoringText,
                ["AmmunitionFamilies[].DisplayName"] = AuthoringText,
                ["Economy.CurrencyDisplayName"] = AuthoringText
            },
            ItemOptionalPaths);

        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    // GameService stores every definition's structural fingerprint and refuses to start when the
    // fingerprint of a definition in use changes. Changing this set is a decision that ships with
    // a migration that restamps the stored values.
    [Fact]
    public void TheItemStructuralFingerprintCoversExactlyThePinnedFields()
    {
        var fields = HashCoverage.FieldsThatChange(
            HashCoverage.Sample<ItemCatalogRuntimeDocument>,
            StampItems,
            catalog => catalog.Definitions.Select(definition => definition.StructuralFingerprint),
            ItemOptionalPaths);

        Assert.Equal(
            [
                "Definitions[].Bag",
                "Definitions[].Bag.CarryCapacityBonus",
                "Definitions[].Bag.Slots[].AcceptedTags[]",
                "Definitions[].Bag.Slots[].Index",
                "Definitions[].Bag.Slots[].Kind",
                "Definitions[].BlockedEquipmentSlots[]",
                "Definitions[].Category",
                "Definitions[].DefaultPolicies[]",
                "Definitions[].EquipmentSlots[]",
                "Definitions[].Id",
                "Definitions[].MaximumStackSize",
                "Definitions[].PlayerDestroyable",
                "Definitions[].Tags[]",
                "Definitions[].UnitWeight"
            ],
            fields);
    }

    [Fact]
    public void TheQuestCatalogStampCoversEveryField()
    {
        var findings = HashCoverage.Check(
            HashCoverage.Sample<QuestCatalogRuntimeDocument>,
            StampQuests,
            "QuestCatalogCompiler.Stamp",
            new Dictionary<string, string>
            {
                ["Quests[].Title"] = ClientText,
                ["Quests[].OfferText"] = ClientText,
                ["Quests[].ProgressText"] = ClientText,
                ["Quests[].CompletionText"] = ClientText,
                ["Quests[].Steps[].Objectives[].Text"] = ClientText,
                ["Quests[].Steps[].Objectives[].Line"] = ClientText
            },
            null);

        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    // GameService stores every quest's structural fingerprint and refuses to start when the
    // fingerprint of a quest in use changes. Changing this set is a decision that ships with a
    // migration that restamps the stored values.
    [Fact]
    public void TheQuestStructuralFingerprintCoversExactlyThePinnedFields()
    {
        var fields = HashCoverage.FieldsThatChange(
            HashCoverage.Sample<QuestCatalogRuntimeDocument>,
            StampQuests,
            catalog => catalog.Quests.Select(quest => quest.StructuralFingerprint),
            null);

        Assert.Equal(
            [
                "Quests[].Id",
                "Quests[].Steps[].Id",
                "Quests[].Steps[].Objectives[].ActorIds[]",
                "Quests[].Steps[].Objectives[].ActorTag",
                "Quests[].Steps[].Objectives[].Id",
                "Quests[].Steps[].Objectives[].ItemId",
                "Quests[].Steps[].Objectives[].Kind",
                "Quests[].Steps[].Objectives[].NpcActorId"
            ],
            fields);
    }

    [Fact]
    public void TheWorldActorCatalogStampCoversEveryField()
    {
        var findings = HashCoverage.Check(
            HashCoverage.Sample<WorldActorCatalogRuntimeDocument>,
            catalog =>
            {
                WorldActorCompiler.StampCatalog(catalog);
                return catalog.Revision;
            },
            "WorldActorCompiler.StampCatalog",
            null,
            null);

        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void TheWorldActorSpawnStampCoversEveryField()
    {
        var findings = HashCoverage.Check(
            HashCoverage.Sample<WorldActorSpawnRuntimeDocument>,
            spawns =>
            {
                WorldActorCompiler.StampSpawns(spawns);
                return spawns.Revision;
            },
            "WorldActorCompiler.StampSpawns",
            null,
            null);

        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void TheTextTableStampCoversEveryField()
    {
        var findings = HashCoverage.Check(
            HashCoverage.Sample<TextTableDocument>,
            document =>
            {
                TextTable.Stamp(document);
                return document.Revision;
            },
            "TextTable.Stamp",
            null,
            null);

        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    private static readonly string[] ItemOptionalPaths =
    [
        "Definitions[].Bag",
        "Definitions[].Weapon",
        "Definitions[].Ammunition"
    ];

    private static string StampItems(ItemCatalogRuntimeDocument catalog)
    {
        ItemCatalogCompiler.Stamp(catalog);
        return catalog.Revision;
    }

    private static string StampQuests(QuestCatalogRuntimeDocument catalog)
    {
        QuestCatalogCompiler.Stamp(catalog);
        return catalog.Revision;
    }
}
