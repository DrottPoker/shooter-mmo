using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Quests;

namespace Mmorpg.SimulationWorker.Tests;

// The worker's copy of a character's quest log: committed changes apply in order and never take
// it back, only for the session that registered it, and reach the client as one message.
public sealed class CharacterQuestLogStoreTests
{
    private static readonly Guid CharacterId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();

    [Fact]
    public void ChangesApplyInOrderAndNeverTakeTheLogBack()
    {
        var store = new CharacterQuestLogStore();
        store.Register(CharacterId, SessionId, CharacterQuestLog.From(new SimulationQuestLogResponse(
            CharacterId,
            "catalog-revision",
            3,
            [new SimulationActiveQuest("quest.wolves", "hunt", DateTime.UtcNow, [new SimulationQuestObjectiveProgress("wolves", 1)])],
            [new SimulationCompletedQuest("quest.ore_run", 2, DateTime.UtcNow)])));

        Assert.True(store.TryApply(CharacterId, SessionId, Change(5, Quest("quest.wolves", null, 1), Quest("quest.ore_run", "deliver", 2)), out var previous));
        Assert.Equal(3, previous);
        Assert.False(store.TryApply(CharacterId, SessionId, Change(5, Quest("quest.wolves", "hunt", 0)), out _));
        Assert.False(store.TryApply(CharacterId, SessionId, Change(4, Quest("quest.wolves", "hunt", 0)), out _));
        Assert.False(store.TryApply(CharacterId, Guid.NewGuid(), Change(6, Quest("quest.wolves", "hunt", 0)), out _));
        Assert.False(store.TryApply(Guid.NewGuid(), SessionId, Change(6, Quest("quest.wolves", "hunt", 0)), out _));

        Assert.True(store.TryGet(CharacterId, SessionId, out var log));
        Assert.Equal(5, log!.Revision);
        var active = Assert.Single(log.ActiveQuests);
        Assert.Equal(("quest.ore_run", "deliver"), (active.Key, active.Value.StepId));
        Assert.Equal(1, log.CompletionCounts["quest.wolves"]);
        Assert.Equal(2, log.CompletionCounts["quest.ore_run"]);

        // An abandoned quest that was never completed leaves no completion behind.
        Assert.True(store.TryApply(CharacterId, SessionId, Change(6, Quest("quest.ore_run", null, 0)), out previous));
        Assert.Equal(5, previous);
        Assert.True(store.TryGet(CharacterId, SessionId, out log));
        Assert.Empty(log!.ActiveQuests);
        Assert.False(log.CompletionCounts.ContainsKey("quest.ore_run"));
    }

    [Fact]
    public void OnlyTheRegisteringSessionRemovesTheLog()
    {
        var store = new CharacterQuestLogStore();
        store.Register(CharacterId, SessionId, Empty(0));

        store.Remove(CharacterId, Guid.NewGuid());
        Assert.True(store.TryGet(CharacterId, SessionId, out _));
        Assert.False(store.TryGet(CharacterId, Guid.NewGuid(), out _));

        store.Remove(CharacterId, SessionId);
        Assert.False(store.TryGet(CharacterId, SessionId, out _));
    }

    [Fact]
    public void AChangeThatOnePacketCannotListAsksForAReload()
    {
        var operationId = Guid.NewGuid();
        var listed = QuestLogChangeMapper.Create(
            operationId,
            3,
            Change(4, Quest("quest.wolves", "hunt", 0, ("wolves", 2)), Quest("quest.letter", null, 1)));

        Assert.False(listed.ReloadRequired);
        Assert.Equal(operationId, listed.OperationId);
        Assert.Equal((3L, 4L), (listed.PreviousQuestLogRevision, listed.QuestLogRevision));
        Assert.Equal(
            ["quest.wolves:hunt:wolves=2:0", "quest.letter:::1"],
            listed.Quests.Select(Describe));

        var tooMany = QuestLogChangeMapper.Create(
            operationId,
            3,
            Change(4, Enumerable.Range(0, RealtimeProtocolLimits.MaximumQuestLogChanges + 1)
                .Select(index => Quest("quest.q" + index, "step", 0))
                .ToArray()));
        var tooLarge = QuestLogChangeMapper.Create(
            operationId,
            3,
            Change(4, Enumerable.Range(0, 8)
                .Select(index => Quest(
                    "quest." + index + new string('q', 100),
                    new string('s', 100),
                    0,
                    Enumerable.Range(0, 8).Select(objective => (objective + new string('o', 100), 1)).ToArray()))
                .ToArray()));

        Assert.All([tooMany, tooLarge], change =>
        {
            Assert.True(change.ReloadRequired);
            Assert.Empty(change.Quests);
            Assert.Equal((3L, 4L), (change.PreviousQuestLogRevision, change.QuestLogRevision));
            Assert.True(RealtimeProtocol.CanEncodeQuestLogChanged(change));
        });
    }

    private static CharacterQuestLog Empty(long revision)
    {
        return CharacterQuestLog.From(new SimulationQuestLogResponse(CharacterId, "catalog-revision", revision, [], []));
    }

    private static SimulationQuestLogChange Change(long revision, params SimulationQuestChange[] quests)
    {
        return new SimulationQuestLogChange(CharacterId, revision, quests);
    }

    private static SimulationQuestChange Quest(
        string questId,
        string? stepId,
        int completions,
        params (string ObjectiveId, int Progress)[] objectives)
    {
        return new SimulationQuestChange(
            questId,
            stepId,
            objectives.Select(objective => new SimulationQuestObjectiveProgress(objective.ObjectiveId, objective.Progress)).ToArray(),
            completions);
    }

    private static string Describe(RealtimeQuestChange quest)
    {
        return quest.QuestId + ":" + quest.StepId + ":"
            + string.Join(",", quest.Objectives.Select(objective => objective.ObjectiveId + "=" + objective.Progress))
            + ":" + quest.CompletionCount;
    }
}
