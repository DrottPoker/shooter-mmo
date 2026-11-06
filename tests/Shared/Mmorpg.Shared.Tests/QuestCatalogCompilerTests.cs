using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Shared.Tests;

// The quest compiler against the real item catalog and the fixture World, which places Mira,
// a city guard, Feral Wolves, and Iron Veins.
public sealed class QuestCatalogCompilerTests
{
    private static readonly Lazy<ItemCatalogRuntimeDocument> Items = new(ItemCatalogTestData.Compile);
    private static readonly Lazy<WorldActorRuntimeDocument> World = new(WorldActorTestData.Compile);

    [Fact]
    public void AValidQuestCompilesToTheSameRevisionEveryTime()
    {
        var first = Compile(KillQuest());
        var second = Compile(KillQuest());

        Assert.Equal(QuestCatalogFormat.Version, first.FormatVersion);
        Assert.Equal(first.Revision, second.Revision);
        var quest = Assert.Single(first.Quests);
        Assert.Equal("quest.test_wolves", quest.Id);
        Assert.Equal(64, quest.StructuralFingerprint.Length);
        Assert.Equal(["mob.feral_wolf"], Assert.Single(Assert.Single(quest.Steps).Objectives).ActorIds);
        QuestCatalogCompiler.ValidateRuntime(first);
    }

    [Fact]
    public void TextsChangeTheRevisionButOnlyStructureChangesTheFingerprint()
    {
        var baseline = Assert.Single(Compile(KillQuest()).Quests);
        var retold = KillQuest();
        retold.OfferText = "The wolves came back.";
        retold.Steps[0].Objectives[0].Count = 5;
        var retoldCatalog = Compile(retold);
        var restructured = KillQuest();
        restructured.Steps[0].Objectives[0].Id = "more_wolves";

        Assert.NotEqual(Compile(KillQuest()).Revision, retoldCatalog.Revision);
        Assert.Equal(baseline.StructuralFingerprint, Assert.Single(retoldCatalog.Quests).StructuralFingerprint);
        Assert.NotEqual(baseline.StructuralFingerprint, Assert.Single(Compile(restructured).Quests).StructuralFingerprint);
    }

    [Fact]
    public void EveryObjectiveKindCompiles()
    {
        var quest = KillQuest();
        quest.StartItems = [Item("quest_item.sealed_letter", 1)];
        quest.Steps =
        [
            Step(
                "work",
                Objective("ore", QuestObjectiveKindIds.Gather, 5, actorIds: ["resource.iron_vein"]),
                Objective("wild", QuestObjectiveKindIds.Kill, 2, actorTag: "wild"),
                Objective("mira", QuestObjectiveKindIds.TalkTo, 1, npcActorId: "npc.city_services", line: "The road is safe."),
                Objective("logs", QuestObjectiveKindIds.Deliver, 2, itemId: "material.oak_log", npcActorId: "npc.city_services")),
            Step(
                "report",
                Objective("pelts", QuestObjectiveKindIds.Collect, 3, itemId: "material.wolf_pelt"),
                Objective("letter", QuestObjectiveKindIds.Collect, 1, itemId: "quest_item.sealed_letter"))
        ];
        quest.RewardChoiceItems = [Item("armor.leather_gloves", 1), Item("armor.leather_boots", 1)];

        var compiled = Assert.Single(Compile(quest).Quests);

        Assert.Equal(2, compiled.Steps.Length);
        Assert.Equal(2, compiled.RewardChoiceItems.Length);
    }

    [Fact]
    public void QuestNpcsMustBeNpcsWithAQuestCapability()
    {
        var mobGiver = KillQuest();
        mobGiver.GiverActorId = "mob.feral_wolf";
        var guardTurnIn = KillQuest();
        guardTurnIn.TurnInActorId = "npc.city_guard";
        var missing = KillQuest();
        missing.GiverActorId = "npc.nobody";

        Assert.Contains(Errors(mobGiver), error => error.StartsWith("quests[0].giverActorId names 'mob.feral_wolf'", StringComparison.Ordinal));
        Assert.Contains(Errors(guardTurnIn), error => error.Contains("not an NPC with the quests capability", StringComparison.Ordinal));
        Assert.Contains(Errors(missing), error => error.Contains("names missing id 'npc.nobody'", StringComparison.Ordinal));
    }

    [Fact]
    public void ObjectiveTargetsMustBeOfTheirKindAndPlacedInAWorld()
    {
        var npcTarget = KillQuest();
        npcTarget.Steps[0].Objectives[0].ActorIds = ["npc.city_guard"];
        var unplaced = KillQuest();
        unplaced.Steps[0].Objectives[0].ActorIds = ["mob.grey_wolf"];
        var mobGather = KillQuest();
        mobGather.Steps = [Step("work", Objective("ore", QuestObjectiveKindIds.Gather, 5, actorIds: ["mob.feral_wolf"]))];
        var unknownTag = KillQuest();
        unknownTag.Steps[0].Objectives[0].ActorIds = [];
        unknownTag.Steps[0].Objectives[0].ActorTag = "dragon";
        var both = KillQuest();
        both.Steps[0].Objectives[0].ActorTag = "wild";

        Assert.Contains(Errors(npcTarget), error => error.Contains("which is not a Mob", StringComparison.Ordinal));
        Assert.Contains(Errors(unplaced), error => error.Contains("which no World places", StringComparison.Ordinal));
        Assert.Contains(Errors(mobGather), error => error.Contains("which is not a resource node", StringComparison.Ordinal));
        Assert.Contains(Errors(unknownTag), error => error.Contains("'dragon' is carried by no Mob", StringComparison.Ordinal));
        Assert.Contains(Errors(both), error => error.EndsWith("must name either actorIds or one actorTag.", StringComparison.Ordinal));
    }

    [Fact]
    public void QuestItemsAndRewardsFollowTheItemRules()
    {
        var ordinaryStart = KillQuest();
        ordinaryStart.StartItems = [Item("material.wolf_pelt", 1)];
        var questReward = KillQuest();
        questReward.RewardItems = [Item("quest_item.sealed_letter", 1)];
        var overStack = KillQuest();
        overStack.RewardItems = [Item("weapon.iron_sword", 2)];
        var unobtainable = KillQuest();
        unobtainable.Steps[0].Objectives = [Objective("letter", QuestObjectiveKindIds.Collect, 1, itemId: "quest_item.sealed_letter")];
        var ordinaryDrop = KillQuest();
        ordinaryDrop.Steps[0].Objectives =
        [
            Objective(
                "pelts",
                QuestObjectiveKindIds.Collect,
                2,
                itemId: "material.wolf_pelt",
                dropSources: [Drop("mob.feral_wolf", 5000)])
        ];
        var badChance = KillQuest();
        badChance.StartItems = [Item("quest_item.sealed_letter", 1)];
        badChance.Steps[0].Objectives =
        [
            Objective(
                "letter",
                QuestObjectiveKindIds.Collect,
                1,
                itemId: "quest_item.sealed_letter",
                dropSources: [Drop("mob.feral_wolf", 0)])
        ];
        var oneChoice = KillQuest();
        oneChoice.RewardChoiceItems = [Item("armor.leather_gloves", 1)];

        Assert.Contains(Errors(ordinaryStart), error => error.Contains("must be a protected quest item", StringComparison.Ordinal));
        Assert.Contains(Errors(questReward), error => error.Contains("cannot be a reward", StringComparison.Ordinal));
        Assert.Contains(Errors(overStack), error => error.Contains(".quantity must be between 1 and 1, one stack.", StringComparison.Ordinal));
        Assert.Contains(Errors(unobtainable), error => error.Contains("neither drops nor grants as often as it takes it", StringComparison.Ordinal));
        Assert.Contains(Errors(ordinaryDrop), error => error.Contains("dropSources may only drop protected quest items", StringComparison.Ordinal));
        Assert.Contains(Errors(badChance), error => error.Contains("chanceBasisPoints must be between 1 and 10000", StringComparison.Ordinal));
        Assert.Contains(Errors(oneChoice), error => error.Contains("rewardChoiceItems must be empty or offer at least 2", StringComparison.Ordinal));
    }

    [Fact]
    public void QuestDropsMustBeProtectedQuestItems()
    {
        var items = ItemCatalogTestData.Compile();
        items.Definitions.Single(definition => definition.Id == "quest_item.wolf_fang").DefaultPolicies = [];
        var fangs = KillQuest();
        fangs.Steps[0].Objectives =
        [
            Objective(
                "fangs",
                QuestObjectiveKindIds.Collect,
                2,
                itemId: "quest_item.wolf_fang",
                dropSources: [Drop("mob.feral_wolf", 5000)])
        ];

        var errors = Assert.Throws<QuestValidationException>(
            () => QuestCatalogCompiler.Compile(Catalog(fangs), items, [World.Value])).Errors;

        Assert.Contains(errors, error => error.Contains("dropSources may only drop protected quest items", StringComparison.Ordinal));
    }

    // Collect objectives count the same carried items, and every quest item that the quest
    // takes must come from its start items or its drops.
    [Fact]
    public void QuestItemsAreTakenNoMoreOftenThanTheQuestSuppliesThem()
    {
        var twoCollectors = KillQuest();
        twoCollectors.Steps[0].Objectives =
        [
            Objective("few_pelts", QuestObjectiveKindIds.Collect, 2, itemId: "material.wolf_pelt"),
            Objective("more_pelts", QuestObjectiveKindIds.Collect, 3, itemId: "material.wolf_pelt")
        ];
        var deliveredThenCollected = KillQuest();
        deliveredThenCollected.StartItems = [Item("quest_item.sealed_letter", 1)];
        deliveredThenCollected.Steps =
        [
            Step(
                "show",
                Objective("show_letter", QuestObjectiveKindIds.Deliver, 1, itemId: "quest_item.sealed_letter", npcActorId: "npc.city_services")),
            Step("return", Objective("letter", QuestObjectiveKindIds.Collect, 1, itemId: "quest_item.sealed_letter"))
        ];
        var deliveredTwice = KillQuest();
        deliveredTwice.StartItems = [Item("quest_item.sealed_letter", 1)];
        deliveredTwice.Steps =
        [
            Step(
                "deliver",
                Objective("first", QuestObjectiveKindIds.Deliver, 1, itemId: "quest_item.sealed_letter", npcActorId: "npc.city_services"),
                Objective("second", QuestObjectiveKindIds.Deliver, 1, itemId: "quest_item.sealed_letter", npcActorId: "npc.city_services"))
        ];
        var suppliedByDrops = KillQuest();
        suppliedByDrops.Steps[0].Objectives =
        [
            Objective(
                "fangs",
                QuestObjectiveKindIds.Collect,
                2,
                itemId: "quest_item.wolf_fang",
                dropSources: [Drop("mob.feral_wolf", 5000)])
        ];
        var suppliedByStartItems = KillQuest();
        suppliedByStartItems.StartItems = [Item("quest_item.sealed_letter", 1)];
        suppliedByStartItems.Steps[0].Objectives =
        [
            Objective("letter", QuestObjectiveKindIds.Collect, 1, itemId: "quest_item.sealed_letter")
        ];

        Assert.Contains(Errors(twoCollectors), error => error.Contains(
            ".itemId 'material.wolf_pelt' is already collected by objective 'few_pelts'",
            StringComparison.Ordinal));
        Assert.Contains(Errors(deliveredThenCollected), error => error.Contains(
            "collects quest item 'quest_item.sealed_letter', which the quest neither drops nor grants as often as it takes it",
            StringComparison.Ordinal));
        Assert.Equal(
            2,
            Errors(deliveredTwice).Count(error => error.Contains(
                "does not grant as often as its deliveries take it",
                StringComparison.Ordinal)));
        Assert.Single(Compile(suppliedByDrops).Quests);
        Assert.Single(Compile(suppliedByStartItems).Quests);
    }

    [Fact]
    public void StepRulesKeepCollectingLastAndTalksAwayFromTheTurnIn()
    {
        var earlyCollect = KillQuest();
        earlyCollect.Steps =
        [
            Step("gather", Objective("pelts", QuestObjectiveKindIds.Collect, 3, itemId: "material.wolf_pelt")),
            Step("hunt", Objective("wolves", QuestObjectiveKindIds.Kill, 1, actorIds: ["mob.feral_wolf"]))
        ];
        var talkToTurnIn = KillQuest();
        talkToTurnIn.Steps =
        [
            Step(
                "report",
                Objective("mira", QuestObjectiveKindIds.TalkTo, 1, npcActorId: "npc.city_services", line: "Here I am."))
        ];
        var deliverToTurnIn = KillQuest();
        deliverToTurnIn.Steps =
        [
            Step(
                "report",
                Objective("pelts", QuestObjectiveKindIds.Deliver, 2, itemId: "material.wolf_pelt", npcActorId: "npc.city_services"))
        ];
        var longTalk = KillQuest();
        longTalk.Steps =
        [
            Step(
                "report",
                Objective("guard", QuestObjectiveKindIds.TalkTo, 2, npcActorId: "npc.city_guard", line: "Hello."))
        ];
        var duplicate = KillQuest();
        duplicate.Steps = [Step("hunt", duplicate.Steps[0].Objectives[0], Objective("wolves", QuestObjectiveKindIds.Kill, 1, actorIds: ["mob.feral_wolf"]))];

        Assert.Contains(Errors(earlyCollect), error => error.Contains("belongs to the last step", StringComparison.Ordinal));
        Assert.Contains(Errors(talkToTurnIn), error => error.Contains("talks to the turn-in NPC in the last step", StringComparison.Ordinal));
        Assert.Contains(Errors(deliverToTurnIn), error => error.Contains("delivers to the turn-in NPC in the last step", StringComparison.Ordinal));
        Assert.Contains(Errors(longTalk), error => error.Contains(".count must be 1 for a conversation.", StringComparison.Ordinal));
        Assert.Contains(Errors(duplicate), error => error.Contains("contains duplicate objective id 'wolves'", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiredQuestsMustExistAndFormNoCycle()
    {
        var first = KillQuest("quest.first");
        var second = KillQuest("quest.second");
        first.RequiredQuestIds = ["quest.second"];
        second.RequiredQuestIds = ["quest.first"];
        var missing = KillQuest("quest.third");
        missing.RequiredQuestIds = ["quest.unknown"];

        var errors = Assert.Throws<QuestValidationException>(() => Compile(first, second, missing)).Errors;

        Assert.Contains(errors, error => error.StartsWith("requiredQuestIds form a cycle: quest.first -> quest.second -> quest.first", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("names missing id 'quest.unknown'", StringComparison.Ordinal));
    }

    [Fact]
    public void TextsLimitsAndIdsAreEnforced()
    {
        var longTitle = KillQuest();
        longTitle.Title = new string('t', QuestContentRules.MaximumTitleLength + 1);
        var controlText = KillQuest();
        controlText.OfferText = "First line.\nSecond line.";
        var richReward = KillQuest();
        richReward.RewardGold = QuestContentRules.MaximumRewardGold + 1;
        var lowLevel = KillQuest();
        lowLevel.MinimumLevel = 0;
        var manySteps = KillQuest();
        manySteps.Steps = Enumerable.Range(0, QuestContentRules.MaximumSteps + 1)
            .Select(index => Step("step_" + index, Objective("wolves_" + index, QuestObjectiveKindIds.Kill, 1, actorIds: ["mob.feral_wolf"])))
            .ToArray();
        var badKind = KillQuest();
        badKind.Steps[0].Objectives[0].Kind = "escort";

        Assert.Contains(Errors(longTitle), error => error.StartsWith("quests[0].title must be one trimmed line", StringComparison.Ordinal));
        Assert.Contains(Errors(controlText), error => error.StartsWith("quests[0].offerText must be one trimmed line", StringComparison.Ordinal));
        Assert.Contains(Errors(richReward), error => error.StartsWith("quests[0].rewardGold must be between", StringComparison.Ordinal));
        Assert.Contains(Errors(lowLevel), error => error.StartsWith("quests[0].minimumLevel must be between 1", StringComparison.Ordinal));
        Assert.Contains(Errors(manySteps), error => error.StartsWith("quests[0].steps must hold between 1 and 8", StringComparison.Ordinal));
        Assert.Contains(Errors(badKind), error => error.EndsWith(".kind has unsupported value 'escort'.", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasedIdsStayUniqueAndRetiredIdsUnused()
    {
        var catalog = Catalog(KillQuest("quest.old"), KillQuest("quest.old"));
        catalog.RetiredQuestIds = ["quest.old"];

        var errors = Assert.Throws<QuestValidationException>(() => Compile(catalog)).Errors;

        Assert.Contains(errors, error => error == "quests contains duplicate id 'quest.old'.");
        Assert.Contains(errors, error => error.Contains("'quest.old' is retired", StringComparison.Ordinal));
    }

    [Fact]
    public void AChangedRuntimeCatalogFailsItsValidation()
    {
        var runtime = Compile(KillQuest());
        runtime.Quests[0].Steps[0].Objectives[0].Kind = QuestObjectiveKindIds.Gather;
        var retitled = Compile(KillQuest());
        retitled.Quests[0].Title = "Another Title";

        Assert.Contains(
            Assert.Throws<QuestValidationException>(() => QuestCatalogCompiler.ValidateRuntime(runtime)).Errors,
            error => error.Contains("stale structural fingerprint", StringComparison.Ordinal));
        Assert.Contains(
            Assert.Throws<QuestValidationException>(() => QuestCatalogCompiler.ValidateRuntime(retitled)).Errors,
            error => error.Contains("revision does not match", StringComparison.Ordinal));
    }

    // A service relies on the shape of a loaded catalog, so it is checked before the hashes.
    [Fact]
    public void ARuntimeCatalogOutsideTheQuestShapeFailsItsValidation()
    {
        var unknownKind = Compile(KillQuest());
        unknownKind.Quests[0].Steps[0].Objectives[0].Kind = "escort";
        var missingRequirement = Compile(KillQuest());
        missingRequirement.Quests[0].RequiredQuestIds = ["quest.missing"];
        var noSteps = Compile(KillQuest());
        noSteps.Quests[0].Steps = [];
        var duplicate = Compile(KillQuest("quest.first"), KillQuest("quest.second"));
        duplicate.Quests[1].Id = "quest.first";

        Assert.Contains(
            Assert.Throws<QuestValidationException>(() => QuestCatalogCompiler.ValidateRuntime(unknownKind)).Errors,
            error => error.Contains("a known kind", StringComparison.Ordinal));
        Assert.Contains(
            Assert.Throws<QuestValidationException>(() => QuestCatalogCompiler.ValidateRuntime(missingRequirement)).Errors,
            error => error.Contains("requires a quest that the catalog does not hold", StringComparison.Ordinal));
        Assert.Contains(
            Assert.Throws<QuestValidationException>(() => QuestCatalogCompiler.ValidateRuntime(noSteps)).Errors,
            error => error.Contains("must hold between 1 and 8 steps", StringComparison.Ordinal));
        Assert.Contains(
            Assert.Throws<QuestValidationException>(() => QuestCatalogCompiler.ValidateRuntime(duplicate)).Errors,
            error => error.Contains("duplicate quest 'quest.first'", StringComparison.Ordinal));
    }

    // The catalog size bounds every list of a character's quests, such as a reset of all of them.
    [Fact]
    public void ACatalogHoldsAtMostTenThousandQuests()
    {
        var crowded = Catalog(Enumerable.Range(0, QuestContentRules.MaximumQuests + 1)
            .Select(index => KillQuest("quest.test_" + index))
            .ToArray());
        var crowdedRuntime = Compile(KillQuest());
        crowdedRuntime.Quests = Enumerable.Repeat(crowdedRuntime.Quests[0], QuestContentRules.MaximumQuests + 1).ToArray();

        Assert.Contains(
            Assert.Throws<QuestValidationException>(() => Compile(crowded)).Errors,
            error => error == "quests must hold at most 10000 quests.");
        Assert.Contains(
            Assert.Throws<QuestValidationException>(() => QuestCatalogCompiler.ValidateRuntime(crowdedRuntime)).Errors,
            error => error == "runtime quest catalog must hold at most 10000 quests.");
    }

    private static QuestCatalogRuntimeDocument Compile(params QuestAuthoringEntry[] quests)
    {
        return Compile(Catalog(quests));
    }

    private static QuestCatalogRuntimeDocument Compile(QuestCatalogAuthoringDocument catalog)
    {
        return QuestCatalogCompiler.Compile(catalog, Items.Value, [World.Value]);
    }

    private static IReadOnlyList<string> Errors(QuestAuthoringEntry quest)
    {
        return Assert.Throws<QuestValidationException>(() => Compile(quest)).Errors;
    }

    private static QuestCatalogAuthoringDocument Catalog(params QuestAuthoringEntry[] quests)
    {
        return new QuestCatalogAuthoringDocument
        {
            FormatVersion = QuestCatalogFormat.Version,
            CatalogId = "core",
            MaximumActiveQuests = 25,
            RetiredQuestIds = [],
            Quests = quests
        };
    }

    private static QuestAuthoringEntry KillQuest(string id = "quest.test_wolves")
    {
        return new QuestAuthoringEntry
        {
            Id = id,
            Title = "Test Wolves",
            GiverActorId = "npc.city_services",
            TurnInActorId = "npc.city_services",
            MinimumLevel = 1,
            RequiredQuestIds = [],
            Repeatable = false,
            Tags = ["test"],
            OfferText = "The wolves grow bold. Hunt three of them.",
            ProgressText = "Are the wolves dealt with?",
            CompletionText = "Well done.",
            StartItems = [],
            Steps = [Step("hunt", Objective("wolves", QuestObjectiveKindIds.Kill, 3, actorIds: ["mob.feral_wolf"]))],
            RewardGold = 10,
            RewardItems = [],
            RewardChoiceItems = []
        };
    }

    private static QuestStepAuthoringEntry Step(string id, params QuestObjectiveAuthoringEntry[] objectives)
    {
        return new QuestStepAuthoringEntry { Id = id, Objectives = objectives };
    }

    private static QuestObjectiveAuthoringEntry Objective(
        string id,
        string kind,
        int count,
        string[]? actorIds = null,
        string actorTag = "",
        string itemId = "",
        string npcActorId = "",
        string line = "",
        QuestDropSourceAuthoringEntry[]? dropSources = null)
    {
        return new QuestObjectiveAuthoringEntry
        {
            Id = id,
            Kind = kind,
            Text = "Objective " + id,
            Count = count,
            ActorIds = actorIds ?? [],
            ActorTag = actorTag,
            ItemId = itemId,
            NpcActorId = npcActorId,
            Line = line,
            DropSources = dropSources ?? []
        };
    }

    private static QuestItemQuantityAuthoringEntry Item(string definitionId, int quantity)
    {
        return new QuestItemQuantityAuthoringEntry { DefinitionId = definitionId, Quantity = quantity };
    }

    private static QuestDropSourceAuthoringEntry Drop(string actorId, int chanceBasisPoints)
    {
        return new QuestDropSourceAuthoringEntry { ActorId = actorId, ChanceBasisPoints = chanceBasisPoints, Quantity = 1 };
    }
}
