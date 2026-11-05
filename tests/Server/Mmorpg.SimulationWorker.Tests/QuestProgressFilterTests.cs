using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Tests.Realtime;

namespace Mmorpg.SimulationWorker.Tests;

// The worker credits a kill or a yield only when an unfinished counted objective of a current
// step asks for it, and leaves the decision to GameService while its quest log is stale.
public sealed class QuestProgressFilterTests
{
    [Fact]
    public void AnUnfinishedObjectiveOfTheCurrentStepAsksForTheKillOrTheYield()
    {
        var session = RealtimeTestSessions.Create();
        var store = new CharacterQuestLogStore();
        var filter = new QuestProgressFilter(store, RealtimeServerHarness.QuestCatalog);
        Register(store, session, ("quest.fixture.wolves", "hunt", 1), ("quest.fixture.errand", "work", 0));

        Assert.True(filter.CountsKill(session, "mob.feral_wolf", ["wolf"]));
        Assert.False(filter.CountsKill(session, "mob.grey_wolf", ["wolf"]));
        Assert.True(filter.CountsGather(session, "resource.iron_vein"));
        Assert.False(filter.CountsGather(session, "resource.oak_tree"));

        // Two wolves finish the hunt, and the errand's return step asks for no ore.
        Register(store, session, ("quest.fixture.wolves", "hunt", 2), ("quest.fixture.errand", "return", 0));
        Assert.False(filter.CountsKill(session, "mob.feral_wolf", ["wolf"]));
        Assert.False(filter.CountsGather(session, "resource.iron_vein"));

        store.MarkStale(session.CharacterId, session.SimulationSessionId);
        Assert.True(filter.CountsKill(session, "mob.feral_wolf", ["wolf"]));

        var unknown = RealtimeTestSessions.Create();
        Assert.False(filter.CountsKill(unknown, "mob.feral_wolf", ["wolf"]));
    }

    // The catalog alone tells whether a kill of a Mob can count for any quest.
    [Fact]
    public void AKillCanCountOnlyForAMobThatAQuestAsksFor()
    {
        var filter = new QuestProgressFilter(new CharacterQuestLogStore(), RealtimeServerHarness.QuestCatalog);

        Assert.True(filter.MayCountKill("mob.feral_wolf", ["wolf"]));
        Assert.False(filter.MayCountKill("mob.grey_wolf", ["wolf"]));
        Assert.False(filter.MayCountKill("resource.iron_vein", []));
    }

    private static void Register(
        CharacterQuestLogStore store,
        Mmorpg.SimulationWorker.Sessions.ActiveSimulationSession session,
        params (string QuestId, string StepId, int Progress)[] quests)
    {
        store.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterQuestLog.From(new SimulationQuestLogResponse(
                session.CharacterId,
                RealtimeServerHarness.QuestCatalog.Revision,
                1,
                quests.Select(quest => new SimulationActiveQuest(
                        quest.QuestId,
                        quest.StepId,
                        DateTime.UtcNow,
                        quest.QuestId == "quest.fixture.wolves"
                            ? [new SimulationQuestObjectiveProgress("wolves", quest.Progress)]
                            : quest.StepId == "work"
                                ? [new SimulationQuestObjectiveProgress("ore", quest.Progress)]
                                : []))
                    .ToArray(),
                [])));
    }
}
