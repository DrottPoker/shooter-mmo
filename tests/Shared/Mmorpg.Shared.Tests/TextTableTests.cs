using Mmorpg.Testing;
using Mmorpg.Testing.Content;
using Mmorpg.Tools.ContentPipeline;
using Mmorpg.Tools.WorldActorCompiler;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Quests;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Shared.Tests;

public sealed class TextTableTests
{
    [Fact]
    public void KeysAreBuiltFromContentIds()
    {
        Assert.Equal("item/weapon.iron_sword/name", TextKeys.ItemName("weapon.iron_sword"));
        Assert.Equal("equipment-slot/main_hand/name", TextKeys.EquipmentSlotName("main_hand"));
        Assert.Equal("currency/name", TextKeys.CurrencyName());
        Assert.Equal("actor/mob.feral_wolf/examine", TextKeys.ActorExamine("mob.feral_wolf"));
        Assert.Equal(
            "actor/npc.city_services/capability/vendor/menu",
            TextKeys.ActorCapabilityMenuOption("npc.city_services", "vendor"));
        Assert.Equal(
            "quest/quest.the_alpha/step/hunt/objective/alpha/text",
            TextKeys.QuestObjectiveText("quest.the_alpha", "hunt", "alpha"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Weapon.sword")]
    [InlineData("weapon/sword")]
    [InlineData("weapon sword")]
    [InlineData(".sword")]
    [InlineData("sword-")]
    public void AnIdThatCannotBeAKeySegmentIsRefused(string id)
    {
        Assert.Throws<ArgumentException>(() => TextKeys.ItemName(id));
    }

    [Theory]
    [InlineData("item/weapon.iron_sword/name", true)]
    [InlineData("currency/name", true)]
    [InlineData("name", false)]
    [InlineData("item//name", false)]
    [InlineData("item/Weapon/name", false)]
    [InlineData("item/weapon/name/", false)]
    [InlineData("", false)]
    public void AKeyIsAPathOfAtLeastTwoSegments(string key, bool valid)
    {
        Assert.Equal(valid, TextKeys.IsValid(key));
    }

    [Fact]
    public void EqualEntriesBuildTheSameTableInAnyOrder()
    {
        var first = TextTable.Create(
            TextTableIds.Items,
            "en",
            [Entry("item/b/name", "Bow"), Entry("item/a/name", "Arrow")]);
        var second = TextTable.Create(
            TextTableIds.Items,
            "en",
            [Entry("item/a/name", "Arrow"), Entry("item/b/name", "Bow")]);

        Assert.Equal(TextTableFormat.Version, first.FormatVersion);
        Assert.Equal(["item/a/name", "item/b/name"], first.Entries.Select(entry => entry.Key));
        Assert.Equal(64, first.Revision.Length);
        Assert.Equal(first.Revision, second.Revision);
        TextTable.Validate(first);
    }

    [Fact]
    public void TheRevisionCoversEveryKeyTextTableAndLocale()
    {
        var baseline = Create(Entry("item/a/name", "Arrow")).Revision;

        Assert.NotEqual(baseline, Create(Entry("item/a/name", "Arrows")).Revision);
        Assert.NotEqual(baseline, Create(Entry("item/b/name", "Arrow")).Revision);
        Assert.NotEqual(
            baseline,
            TextTable.Create(TextTableIds.Actors, "en", [Entry("item/a/name", "Arrow")]).Revision);
        Assert.NotEqual(
            baseline,
            TextTable.Create(TextTableIds.Items, "sv", [Entry("item/a/name", "Arrow")]).Revision);
    }

    // Two entries must never hash like one entry whose text holds a line feed.
    [Fact]
    public void ATextWithALineFeedDoesNotCollideWithTwoEntries()
    {
        var joined = Create(Entry("item/a/name", "Arrow\nitem/b/name\n3\nBow"));
        var separate = Create(Entry("item/a/name", "Arrow"), Entry("item/b/name", "Bow"));

        Assert.NotEqual(joined.Revision, separate.Revision);
    }

    [Theory]
    [InlineData("item/a/name", "", "is required")]
    [InlineData("item/a/name", " Arrow", "whitespace")]
    [InlineData("item/a/name", "Arrow\t", "whitespace")]
    [InlineData("item/a/name", "Ar\rrow", "control characters")]
    [InlineData("Item/a/name", "Arrow", "not a valid text key")]
    public void AnInvalidEntryFailsTheTable(string key, string text, string expectedError)
    {
        var exception = Assert.Throws<TextTableValidationException>(() => Create(Entry(key, text)));

        Assert.Contains(expectedError, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextOverTheLimitFailsTheTable()
    {
        var exception = Assert.Throws<TextTableValidationException>(
            () => Create(Entry("item/a/name", new string('a', TextTableFormat.MaximumTextLength + 1))));

        Assert.Contains("at most", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyListedTwiceFailsTheTable()
    {
        var exception = Assert.Throws<TextTableValidationException>(
            () => Create(Entry("item/a/name", "Arrow"), Entry("item/a/name", "Bolt")));

        Assert.Contains("listed twice", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("weapons", "en")]
    [InlineData(TextTableIds.Items, "EN")]
    [InlineData(TextTableIds.Items, "en_US")]
    [InlineData(TextTableIds.Items, "en-")]
    [InlineData(TextTableIds.Items, "")]
    public void AnUnknownTableOrAMalformedLocaleFailsTheTable(string tableId, string locale)
    {
        Assert.Throws<TextTableValidationException>(
            () => TextTable.Create(tableId, locale, [Entry("item/a/name", "Arrow")]));
    }

    [Fact]
    public void ALoadedTableMustBeExactlyWhatWasWritten()
    {
        var edited = Create(Entry("item/a/name", "Arrow"));
        edited.Entries[0].Text = "Bolt";
        var unordered = Create(Entry("item/a/name", "Arrow"), Entry("item/b/name", "Bow"));
        Array.Reverse(unordered.Entries);
        var wrongVersion = Create(Entry("item/a/name", "Arrow"));
        wrongVersion.FormatVersion++;

        Assert.Contains(
            "revision does not match",
            Assert.Throws<TextTableValidationException>(() => TextTable.Validate(edited)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "not in key order",
            Assert.Throws<TextTableValidationException>(() => TextTable.Validate(unordered)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "formatVersion",
            Assert.Throws<TextTableValidationException>(() => TextTable.Validate(wrongVersion)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheItemTableNamesEveryDefinitionAndTheCatalogVocabulary()
    {
        var catalog = ItemCatalogTestData.LoadAuthoring();
        var table = ContentText.FromItemCatalog(catalog);
        var texts = table.Entries.ToDictionary(entry => entry.Key, entry => entry.Text, StringComparer.Ordinal);

        Assert.Equal(TextTableIds.Items, table.TableId);
        Assert.Equal(TextTableFormat.SourceLocale, table.Locale);
        Assert.All(
            catalog.Definitions,
            definition => Assert.Equal(definition.DisplayName, texts[TextKeys.ItemName(definition.Id)]));
        Assert.All(
            catalog.EquipmentSlots,
            slot => Assert.Equal(slot.DisplayName, texts[TextKeys.EquipmentSlotName(slot.Id)]));
        Assert.All(
            catalog.Categories,
            category => Assert.Equal(category.DisplayName, texts[TextKeys.ItemCategoryName(category.Id)]));
        Assert.All(
            catalog.Vendors,
            vendor => Assert.Equal(vendor.DisplayName, texts[TextKeys.VendorName(vendor.Id)]));
        Assert.Equal(catalog.Economy.CurrencyDisplayName, texts[TextKeys.CurrencyName()]);
        Assert.Equal("Iron Sword", texts["item/weapon.iron_sword/name"]);
    }

    [Fact]
    public void TheActorTableHoldsNamesExamineTextsAndCapabilityTexts()
    {
        var actors = WorldActorTestData.LoadActors();
        var texts = ContentText.FromWorldActors(actors).Entries
            .ToDictionary(entry => entry.Key, entry => entry.Text, StringComparer.Ordinal);

        Assert.All(
            actors.Actors,
            actor => Assert.Equal(actor.DisplayName, texts[TextKeys.ActorName(actor.Id)]));
        Assert.All(
            actors.Actors.Where(actor => !string.IsNullOrEmpty(actor.ExamineText)),
            actor => Assert.Equal(actor.ExamineText, texts[TextKeys.ActorExamine(actor.Id)]));
        var services = actors.Actors.Single(actor => actor.Id == "npc.city_services");
        Assert.NotEmpty(services.Capabilities);
        Assert.All(
            services.Capabilities,
            capability =>
            {
                Assert.Equal(
                    capability.DisplayName,
                    texts[TextKeys.ActorCapabilityName(services.Id, capability.Id)]);
                Assert.Equal(
                    capability.MenuOption,
                    texts[TextKeys.ActorCapabilityMenuOption(services.Id, capability.Id)]);
            });
        Assert.All(
            actors.Factions,
            faction => Assert.Equal(faction.DisplayName, texts[TextKeys.FactionName(faction.Id)]));
    }

    // The compiled actors hold rules only, so a new text changes the text table and no World.
    [Fact]
    public void AnActorTextChangeChangesNoCompiledWorld()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        var original = WorldActorCompiler.Compile(actors, spawns);
        var originalTexts = ContentText.FromWorldActors(actors);
        var actor = actors.Actors.First(candidate => candidate.Capabilities is { Length: > 0 });
        actor.DisplayName += " Renamed";
        actor.ExamineText = "A new examine text.";
        actor.Capabilities[0].DisplayName += " Renamed";
        actor.Capabilities[0].MenuOption = "Renamed";
        actors.Factions[0].DisplayName += " Renamed";
        actors.PresentationArchetypes[0].DisplayName += " Renamed";

        var changed = WorldActorCompiler.Compile(actors, spawns);

        Assert.Equal(original.Revision, changed.Revision);
        Assert.Equal(WorldActorJson.SerializeRuntime(original), WorldActorJson.SerializeRuntime(changed));
        Assert.NotEqual(originalTexts.Revision, ContentText.FromWorldActors(actors).Revision);
        var json = WorldActorJson.SerializeRuntime(changed);
        Assert.DoesNotContain("isplayName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("xamineText", json, StringComparison.Ordinal);
        Assert.DoesNotContain("enuOption", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQuestTableHoldsEveryQuestTextAndALineOnlyWhereOneIsWritten()
    {
        var catalog = QuestCatalogTestData.Compile();
        var texts = ContentText.FromQuestCatalog(catalog).Entries
            .ToDictionary(entry => entry.Key, entry => entry.Text, StringComparer.Ordinal);

        Assert.All(
            catalog.Quests,
            quest =>
            {
                Assert.Equal(quest.Title, texts[TextKeys.QuestTitle(quest.Id)]);
                Assert.Equal(quest.OfferText, texts[TextKeys.QuestOffer(quest.Id)]);
                Assert.Equal(quest.ProgressText, texts[TextKeys.QuestProgress(quest.Id)]);
                Assert.Equal(quest.CompletionText, texts[TextKeys.QuestCompletion(quest.Id)]);
                foreach (var step in quest.Steps)
                {
                    foreach (var objective in step.Objectives)
                    {
                        Assert.Equal(
                            objective.Text,
                            texts[TextKeys.QuestObjectiveText(quest.Id, step.Id, objective.Id)]);
                        var lineKey = TextKeys.QuestObjectiveLine(quest.Id, step.Id, objective.Id);
                        if (string.IsNullOrEmpty(objective.Line))
                        {
                            Assert.DoesNotContain(lineKey, texts.Keys);
                        }
                        else
                        {
                            Assert.Equal(objective.Line, texts[lineKey]);
                        }
                    }
                }
            });
        Assert.Contains(
            catalog.Quests.SelectMany(quest => quest.Steps).SelectMany(step => step.Objectives),
            objective => objective.Kind == QuestObjectiveKindIds.TalkTo && !string.IsNullOrEmpty(objective.Line));
    }

    // The compilers write these files, and --verify checks them in CI. This test fails the same
    // way without running a tool, and reads each file back as a client would.
    [Theory]
    [InlineData(TextTableIds.Items)]
    [InlineData(TextTableIds.Actors)]
    [InlineData(TextTableIds.Quests)]
    public void TheCheckedInTableMatchesItsContent(string tableId)
    {
        var expected = tableId switch
        {
            TextTableIds.Items => ContentText.FromItemCatalog(ItemCatalogTestData.LoadAuthoring()),
            TextTableIds.Actors => ContentText.FromWorldActors(WorldActorTestData.LoadActors()),
            _ => ContentText.FromQuestCatalog(QuestCatalogTestData.Compile())
        };
        var json = StrictContentJson.NormalizeLineEndings(File.ReadAllText(
            ContentLayout.GeneratedTextTablePath(
                RepositoryPaths.Generated,
                TextTableFormat.SourceLocale,
                tableId)));

        Assert.Equal(TextTableOutput.Serialize(expected), json);
        Assert.Equal(expected.Revision, TextTableOutput.Deserialize(json).Revision);
    }

    [Fact]
    public void EveryKnownTableHasItsOwnFile()
    {
        var paths = TextTableIds.All
            .Select(tableId => ContentLayout.GeneratedTextTablePath("generated", "en", tableId))
            .ToArray();

        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
        Assert.All(paths, path => Assert.EndsWith(TextTableFormat.FileSuffix, path, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(
            () => ContentLayout.GeneratedTextTablePath("generated", "en", "weapons"));
        Assert.Throws<InvalidDataException>(
            () => ContentLayout.GeneratedTextTablePath("generated", "../en", TextTableIds.Items));
    }

    [Fact]
    public void ACatalogLooksUpEveryTextOfItsTables()
    {
        var items = Create(Entry("item/a/name", "Arrow"));
        var quests = TextTable.Create(TextTableIds.Quests, "en", [Entry("quest/q/title", "A Quest")]);

        var catalog = TextCatalog.Create([quests, items]);

        Assert.Equal("en", catalog.Locale);
        Assert.Equal(2, catalog.Count);
        Assert.Equal(64, catalog.Revision.Length);
        Assert.Equal("Arrow", catalog.Get("item/a/name"));
        Assert.Equal("A Quest", catalog.Get("quest/q/title"));
        Assert.True(catalog.TryGet("item/a/name", out var name));
        Assert.Equal("Arrow", name);
        Assert.Equal(catalog.Revision, TextCatalog.Create([items, quests]).Revision);
    }

    // A missing text must be seen, not pass as an empty label.
    [Fact]
    public void AMissingTextShowsItsKeyUnlessTheTextIsOptional()
    {
        var catalog = TextCatalog.Create([Create(Entry("item/a/name", "Arrow"))]);

        Assert.Equal("[item/b/name]", catalog.Get("item/b/name"));
        Assert.Equal("[]", catalog.Get(null!));
        Assert.Equal(string.Empty, catalog.GetOrEmpty("item/b/name"));
        Assert.Equal("Arrow", catalog.GetOrEmpty("item/a/name"));
        Assert.False(catalog.TryGet("item/b/name", out _));
        Assert.False(catalog.TryGet(null!, out _));
    }

    [Fact]
    public void TheCatalogRevisionChangesWithAnyTable()
    {
        var quests = TextTable.Create(TextTableIds.Quests, "en", [Entry("quest/q/title", "A Quest")]);
        var baseline = TextCatalog.Create([Create(Entry("item/a/name", "Arrow")), quests]).Revision;

        Assert.NotEqual(baseline, TextCatalog.Create([Create(Entry("item/a/name", "Bolt")), quests]).Revision);
        Assert.NotEqual(baseline, TextCatalog.Create([Create(Entry("item/a/name", "Arrow"))]).Revision);
    }

    [Fact]
    public void ACatalogRefusesTablesThatDoNotBelongTogether()
    {
        var items = Create(Entry("item/a/name", "Arrow"));
        var swedish = TextTable.Create(TextTableIds.Quests, "sv", [Entry("quest/q/title", "Ett uppdrag")]);
        var sameKey = TextTable.Create(TextTableIds.Quests, "en", [Entry("item/a/name", "Arrow again")]);
        var tampered = Create(Entry("item/a/name", "Arrow"));
        tampered.Entries[0].Text = "Bolt";

        Assert.Contains(
            "at least one",
            Assert.Throws<TextTableValidationException>(() => TextCatalog.Create([])).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "locale",
            Assert.Throws<TextTableValidationException>(() => TextCatalog.Create([items, swedish])).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "listed twice",
            Assert.Throws<TextTableValidationException>(() => TextCatalog.Create([items, items])).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "more than one table",
            Assert.Throws<TextTableValidationException>(() => TextCatalog.Create([items, sameKey])).Message,
            StringComparison.Ordinal);
        Assert.Throws<TextTableValidationException>(() => TextCatalog.Create([tampered]));
    }

    [Fact]
    public void AnEmptyCatalogShowsEveryTextAsItsKey()
    {
        var catalog = TextCatalog.Empty("en");

        Assert.Equal(0, catalog.Count);
        Assert.Equal("en", catalog.Locale);
        Assert.Equal("[item/a/name]", catalog.Get("item/a/name"));
    }

    // Unity reads the tables through this camelCase shape.
    [Fact]
    public void TheUnityFieldShapeReadsEveryTableField()
    {
        var table = Create(Entry("item/a/name", "Arrow"), Entry("item/b/name", "Bow"));
        var json = new TextTableJsonDocument
        {
            formatVersion = table.FormatVersion,
            tableId = table.TableId,
            locale = table.Locale,
            revision = table.Revision,
            entries = table.Entries
                .Select(entry => new TextTableJsonEntry { key = entry.Key, text = entry.Text })
                .ToArray()
        };

        var read = json.ToRuntime();

        TextTable.Validate(read);
        Assert.Equal(table.Revision, read.Revision);
        Assert.Equal(["Arrow", "Bow"], read.Entries.Select(entry => entry.Text));
        Assert.Empty(new TextTableJsonDocument().ToRuntime().Entries);
    }

    private static TextTableDocument Create(params TextTableEntry[] entries)
    {
        return TextTable.Create(TextTableIds.Items, TextTableFormat.SourceLocale, entries);
    }

    private static TextTableEntry Entry(string key, string text)
    {
        return new TextTableEntry { Key = key, Text = text };
    }
}
