using Mmorpg.WorldData.Quests;

namespace Mmorpg.Shared.Tests;

public sealed class QuestRulesTests
{
    [Fact]
    public void AQuestIsAvailableOnlyWhenEveryRuleHolds()
    {
        var quest = Quest(minimumLevel: 3, required: ["quest.first"]);

        Assert.Equal(QuestAvailability.Available, Evaluate(quest, level: 3, completed: ["quest.first"]));
        Assert.Equal(QuestAvailability.AlreadyActive, Evaluate(quest, level: 3, completed: ["quest.first"], active: true));
        Assert.Equal(QuestAvailability.AlreadyCompleted, Evaluate(quest, level: 3, completed: ["quest.first", quest.Id]));
        Assert.Equal(QuestAvailability.MissingRequiredQuest, Evaluate(quest, level: 3, completed: []));
        Assert.Equal(QuestAvailability.BelowMinimumLevel, Evaluate(quest, level: 2, completed: ["quest.first"]));
        Assert.Equal(QuestAvailability.QuestLogFull, Evaluate(quest, level: 3, completed: ["quest.first"], activeCount: 25));
    }

    // A quest that only needs a higher level shows a grey marker, so the level is checked after
    // everything else that would hide the quest.
    [Fact]
    public void TheLevelIsTheLastRuleBeforeTheQuestLog()
    {
        var quest = Quest(minimumLevel: 5, required: ["quest.first"]);

        Assert.Equal(QuestAvailability.MissingRequiredQuest, Evaluate(quest, level: 1, completed: []));
        Assert.Equal(QuestAvailability.BelowMinimumLevel, Evaluate(quest, level: 1, completed: ["quest.first"], activeCount: 25));
    }

    // A delivery hands over what its objective still lacks, so a count raised after a part was
    // handed over asks only for the rest.
    [Fact]
    public void ADeliveryHandsOverOnlyWhatItsObjectiveStillLacks()
    {
        var objective = Objective("letters", 5);

        Assert.Equal(5, QuestRules.RemainingDeliveryQuantity(objective, 0));
        Assert.Equal(2, QuestRules.RemainingDeliveryQuantity(objective, 3));
        Assert.Equal(0, QuestRules.RemainingDeliveryQuantity(objective, 7));
    }

    // The giver grants a start item again only for what the quest still needs after its
    // deliveries took their part, and never what the character still owns.
    [Fact]
    public void AStartItemIsGrantedAgainOnlyForWhatTheDeliveriesHaveNotTaken()
    {
        var quest = Quest(steps:
        [
            new QuestStepDefinition { Id = "show", Objectives = [Deliver("show", "quest_item.letter", 1)] },
            new QuestStepDefinition
            {
                Id = "send",
                Objectives = [Deliver("send", "quest_item.letter", 2), Deliver("pelts", "material.wolf_pelt", 3)]
            }
        ]);
        quest.StartItems = [new QuestItemQuantityDefinition { DefinitionId = "quest_item.letter", Quantity = 4 }];
        var progress = new Dictionary<string, int>(StringComparer.Ordinal);

        Assert.Equal(0, QuestRules.DeliveredQuantity(quest, 0, id => progress.GetValueOrDefault(id), "quest_item.letter"));
        Assert.Equal(1, QuestRules.DeliveredQuantity(quest, 1, id => progress.GetValueOrDefault(id), "quest_item.letter"));
        progress["send"] = 2;
        Assert.Equal(3, QuestRules.DeliveredQuantity(quest, 1, id => progress.GetValueOrDefault(id), "quest_item.letter"));
        Assert.Equal(0, QuestRules.DeliveredQuantity(quest, 1, id => progress.GetValueOrDefault(id), "material.ore"));

        Assert.Equal(1, QuestRules.MissingStartItemQuantity(quest, quest.StartItems[0], 1, id => progress.GetValueOrDefault(id), 0));
        Assert.Equal(0, QuestRules.MissingStartItemQuantity(quest, quest.StartItems[0], 1, id => progress.GetValueOrDefault(id), 1));
        Assert.Equal(4, QuestRules.MissingStartItemQuantity(quest, quest.StartItems[0], 0, id => progress.GetValueOrDefault(id), 0));
    }

    [Fact]
    public void ARepeatableQuestIsAvailableAgainAfterItsTurnIn()
    {
        var quest = Quest(repeatable: true);

        Assert.Equal(QuestAvailability.Available, Evaluate(quest, level: 1, completed: [quest.Id]));
    }

    [Fact]
    public void OnlyTheCurrentStepCountsAndTheLastStepMakesTheQuestReady()
    {
        var quest = Quest(steps:
        [
            new QuestStepDefinition { Id = "hunt", Objectives = [Objective("wolves", 3)] },
            new QuestStepDefinition { Id = "report", Objectives = [Objective("guard", 1), Objective("pelts", 2)] }
        ]);
        var progress = new Dictionary<string, int> { ["wolves"] = 3, ["guard"] = 1, ["pelts"] = 1 };

        Assert.Equal(["wolves"], QuestRules.CurrentObjectives(quest, 0).Select(objective => objective.Id));
        Assert.Empty(QuestRules.CurrentObjectives(quest, 2));
        Assert.True(QuestRules.IsStepComplete(quest.Steps[0], id => progress.GetValueOrDefault(id)));
        Assert.False(QuestRules.IsReadyToTurnIn(quest, 0, id => progress.GetValueOrDefault(id)));
        Assert.False(QuestRules.IsReadyToTurnIn(quest, 1, id => progress.GetValueOrDefault(id)));
        progress["pelts"] = 4;
        Assert.True(QuestRules.IsReadyToTurnIn(quest, 1, id => progress.GetValueOrDefault(id)));
    }

    [Fact]
    public void KillsCountForTheNamedMobsOrTheirTagAndYieldsForTheNamedNodes()
    {
        var byId = new QuestObjectiveDefinition
        {
            Id = "wolves",
            Kind = QuestObjectiveKindIds.Kill,
            Count = 2,
            ActorIds = ["mob.feral_wolf"],
            ActorTag = ""
        };
        var byTag = new QuestObjectiveDefinition
        {
            Id = "wild",
            Kind = QuestObjectiveKindIds.Kill,
            Count = 2,
            ActorIds = [],
            ActorTag = "wild"
        };
        var ore = new QuestObjectiveDefinition
        {
            Id = "ore",
            Kind = QuestObjectiveKindIds.Gather,
            Count = 5,
            ActorIds = ["resource.iron_vein"],
            ActorTag = ""
        };
        var pelts = new QuestObjectiveDefinition
        {
            Id = "pelts",
            Kind = QuestObjectiveKindIds.Collect,
            Count = 3,
            ActorIds = [],
            ActorTag = ""
        };

        Assert.True(QuestRules.CountsKill(byId, "mob.feral_wolf", []));
        Assert.False(QuestRules.CountsKill(byId, "mob.grey_wolf", ["wild"]));
        Assert.True(QuestRules.CountsKill(byTag, "mob.grey_wolf", ["wolf", "wild"]));
        Assert.False(QuestRules.CountsKill(byTag, "mob.grey_wolf", ["wolf"]));
        Assert.False(QuestRules.CountsKill(ore, "resource.iron_vein", []));
        Assert.True(QuestRules.CountsGather(ore, "resource.iron_vein"));
        Assert.False(QuestRules.CountsGather(ore, "resource.oak_tree"));
        Assert.False(QuestRules.CountsGather(byId, "mob.feral_wolf"));
        Assert.True(QuestRules.StoresProgress(byId));
        Assert.False(QuestRules.StoresProgress(pelts));
    }

    [Fact]
    public void ACollectObjectiveNamesTheMobsThatDropItsQuestItem()
    {
        var fangs = new QuestObjectiveDefinition
        {
            Id = "fangs",
            Kind = QuestObjectiveKindIds.Collect,
            Count = 2,
            ActorIds = [],
            ActorTag = "",
            ItemId = "quest_item.wolf_fang",
            DropSources = [new QuestDropSourceDefinition { ActorId = "mob.feral_wolf", ChanceBasisPoints = 5000, Quantity = 1 }]
        };
        var wolves = new QuestObjectiveDefinition
        {
            Id = "wolves",
            Kind = QuestObjectiveKindIds.Kill,
            Count = 2,
            ActorIds = ["mob.feral_wolf"],
            ActorTag = "",
            DropSources = [new QuestDropSourceDefinition { ActorId = "mob.feral_wolf", ChanceBasisPoints = 5000, Quantity = 1 }]
        };

        Assert.Equal(5000, QuestRules.FindDropSource(fangs, "mob.feral_wolf")!.ChanceBasisPoints);
        Assert.Null(QuestRules.FindDropSource(fangs, "mob.grey_wolf"));
        Assert.Null(QuestRules.FindDropSource(wolves, "mob.feral_wolf"));
    }

    private static QuestAvailability Evaluate(
        QuestDefinition quest,
        int level,
        string[] completed,
        bool active = false,
        int activeCount = 0)
    {
        return QuestRules.Evaluate(
            quest,
            level,
            activeCount,
            25,
            _ => active,
            id => completed.Contains(id) ? 1 : 0);
    }

    private static QuestDefinition Quest(
        int minimumLevel = 1,
        string[]? required = null,
        bool repeatable = false,
        QuestStepDefinition[]? steps = null)
    {
        return new QuestDefinition
        {
            Id = "quest.second",
            MinimumLevel = minimumLevel,
            RequiredQuestIds = required ?? [],
            Repeatable = repeatable,
            Steps = steps ?? [new QuestStepDefinition { Id = "hunt", Objectives = [Objective("wolves", 3)] }]
        };
    }

    private static QuestObjectiveDefinition Objective(string id, int count)
    {
        return new QuestObjectiveDefinition { Id = id, Kind = QuestObjectiveKindIds.Kill, Count = count };
    }

    private static QuestObjectiveDefinition Deliver(string id, string itemId, int count)
    {
        return new QuestObjectiveDefinition
        {
            Id = id,
            Kind = QuestObjectiveKindIds.Deliver,
            Count = count,
            ItemId = itemId,
            NpcActorId = "npc.city_guard"
        };
    }
}
