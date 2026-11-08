using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.Tests.Realtime;

namespace Mmorpg.SimulationWorker.Tests;

// A dying Feral Wolf drops a stolen trophy for quest.fixture.trophies at 5000 basis points: the
// worker rolls it once for a character whose current step collects the trophies, and for every
// quest that the Mob drops for while its quest log is stale.
public sealed class QuestDropRollerTests
{
    private const string Trophies = "quest.fixture.trophies";
    private const string Wolf = "mob.feral_wolf";

    [Fact]
    public void AnActiveCollectStepRollsTheMobsQuestDrop()
    {
        var session = RealtimeTestSessions.Create();
        var store = new CharacterQuestLogStore();
        var roller = new QuestDropRoller(store, RealtimeServerHarness.QuestCatalog);
        Register(store, session, (Trophies, "collect"), ("quest.fixture.wolves", "hunt"));
        var success = new ScriptedRoll(4999);

        var drops = roller.Roll(session, Wolf, success);

        Assert.Equal([new MobCorpseQuestDropSeed(Trophies, "fixture.trophy", 1)], drops);
        Assert.Equal([(0, 9999)], success.Calls);
        Assert.Empty(roller.Roll(session, Wolf, new ScriptedRoll(5000)));
        Assert.Empty(roller.Roll(session, "mob.grey_wolf", new ScriptedRoll(0)));
    }

    [Fact]
    public void OnlyACharacterWhoseCurrentStepCollectsTheItemRolls()
    {
        var session = RealtimeTestSessions.Create();
        var store = new CharacterQuestLogStore();
        var roller = new QuestDropRoller(store, RealtimeServerHarness.QuestCatalog);
        var unknown = new ScriptedRoll(0);
        var withoutTheQuest = new ScriptedRoll(0);

        var beforeJoin = roller.Roll(session, Wolf, unknown);
        Register(store, session, ("quest.fixture.wolves", "hunt"));
        var hunting = roller.Roll(session, Wolf, withoutTheQuest);

        Assert.Empty(beforeJoin);
        Assert.Empty(hunting);
        Assert.Empty(unknown.Calls);
        Assert.Empty(withoutTheQuest.Calls);
    }

    // A stale log may miss the accepted quest, so every quest that the Mob drops for rolls, and
    // GameService leaves out what the character does not need.
    [Fact]
    public void AStaleQuestLogRollsForEveryQuestTheMobDropsFor()
    {
        var session = RealtimeTestSessions.Create();
        var store = new CharacterQuestLogStore();
        var roller = new QuestDropRoller(store, RealtimeServerHarness.QuestCatalog);
        Register(store, session);
        store.MarkStale(session.CharacterId, session.SimulationSessionId);

        var drops = roller.Roll(session, Wolf, new ScriptedRoll(0));

        Assert.Equal([new MobCorpseQuestDropSeed(Trophies, "fixture.trophy", 1)], drops);
    }

    [Fact]
    public void SyntheticBotsNeverRollQuestDrops()
    {
        var session = RealtimeTestSessions.Create() with { IsSyntheticBot = true };
        var store = new CharacterQuestLogStore();
        var roller = new QuestDropRoller(store, RealtimeServerHarness.QuestCatalog);
        Register(store, session, (Trophies, "collect"));

        Assert.Empty(roller.Roll(session, Wolf, new ScriptedRoll(0)));
    }

    private static void Register(
        CharacterQuestLogStore store,
        ActiveSimulationSession session,
        params (string QuestId, string StepId)[] quests)
    {
        store.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterQuestLog.From(new SimulationQuestLogResponse(
                session.CharacterId,
                RealtimeServerHarness.QuestCatalog.Revision,
                1,
                quests.Select(quest => new SimulationActiveQuest(quest.QuestId, quest.StepId, DateTime.UtcNow, []))
                    .ToArray(),
                [])));
    }

    // Answers every roll with one value and records the bounds it was asked for.
    private sealed class ScriptedRoll(int value) : ICombatRandom
    {
        public List<(int Minimum, int Maximum)> Calls { get; } = [];

        public int NextInclusive(int minimum, int maximum)
        {
            Calls.Add((minimum, maximum));
            return value;
        }
    }
}
