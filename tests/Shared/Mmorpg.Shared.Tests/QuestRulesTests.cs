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
}
