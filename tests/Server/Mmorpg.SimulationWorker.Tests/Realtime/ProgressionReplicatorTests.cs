using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// What the progression replicator keeps and sends for a committed change: the owner hears of a
// gain, a level change, or a state it did not have, a higher level reaches the viewers but not the
// owner, a change without a peer is kept, and a character whose entity went keeps nothing.
public sealed class ProgressionReplicatorTests
{
    [Fact]
    public void AGainReachesTheOwnerAndALevelUpTheViewersButNotTheOwner()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog());
        var owner = fixture.Join(1, "Progressing Owner");
        var viewer = fixture.Join(2, "Progression Viewer");
        fixture.ShowEveryoneToEveryone();
        var replicator = Create(fixture);

        replicator.Apply(owner.Context.Session!, owner.Peer, Change(2, 30, 400, 1, 130, 1), RealtimeProgressionChangeReason.MobKill);

        var progression = Assert.Single(Progressions(owner));
        Assert.Equal(
            ((ushort)2, 30L, 400L, 1, 130L, 1, RealtimeProgressionChangeReason.MobKill),
            (progression.Level, progression.Experience, progression.ExperienceToNextLevel, progression.HighestMobLevelWithoutExperience, progression.ExperienceGained, progression.LevelsGained, progression.Reason));
        var levelUp = Assert.Single(LevelUps(viewer));
        Assert.Equal((owner.EntityId, (ushort)2), (levelUp.EntityId, levelUp.Level));
        Assert.Empty(LevelUps(owner));
        Assert.Empty(Progressions(viewer));
    }

    // A change that gains nothing and leaves the kept state as it was sends nothing; one that
    // differs from it heals the owner's view.
    [Fact]
    public void TheOwnerHearsOfAnUnchangedStateOnlyWhenItDiffersFromTheKeptOne()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog());
        var owner = fixture.Join(1, "Steady Owner");
        var replicator = Create(fixture);
        var session = owner.Context.Session!;

        replicator.Apply(session, owner.Peer, Change(5, 10, 900, 0, 0, 0), RealtimeProgressionChangeReason.QuestReward);
        replicator.Apply(session, owner.Peer, Change(5, 10, 900, 0, 0, 0), RealtimeProgressionChangeReason.QuestReward);
        replicator.Apply(session, owner.Peer, Change(5, 40, 900, 0, 0, 0), RealtimeProgressionChangeReason.QuestReward);

        Assert.Equal([10L, 40L], Progressions(owner).Select(value => value.Experience));
    }

    [Fact]
    public void AChangeWithoutAPeerIsKeptAndDecidesWhichMobsGiveExperience()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog());
        var owner = fixture.Join(1, "Absent Owner");
        var replicator = Create(fixture);
        var session = owner.Context.Session!;
        Assert.True(replicator.GivesExperience(session.CharacterId, 1));

        replicator.Apply(session, null, Change(20, 0, 5000, 12, 900, 2), RealtimeProgressionChangeReason.MobKill);

        Assert.Empty(Progressions(owner));
        Assert.False(replicator.GivesExperience(session.CharacterId, 12));
        Assert.True(replicator.GivesExperience(session.CharacterId, 13));

        replicator.Apply(session, null, Change(200, 0, 0, int.MaxValue, 5000, 180), RealtimeProgressionChangeReason.MobKill);
        Assert.False(replicator.GivesExperience(session.CharacterId, 250));

        // The entity went, and its kept progression with it, so GameService decides again.
        replicator.OnPlayerRemoved(owner.Context, session, owner.EntityId);
        Assert.True(replicator.GivesExperience(session.CharacterId, 250));
        Assert.Equal(0, replicator.Count);
    }

    [Fact]
    public void AChangeOfASessionWhoseEntityWentIsNotKept()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog());
        var owner = fixture.Join(1, "Gone Owner");
        var replicator = Create(fixture);
        var other = RealtimeTestSessions.Create("Never Joined");

        replicator.Apply(other, owner.Peer, Change(3, 0, 100, 0, 50, 1), RealtimeProgressionChangeReason.MobKill);

        Assert.Equal(0, replicator.Count);
        Assert.Empty(Progressions(owner));
    }

    // A body's older session commits after the character joined again with a new session: a
    // change ahead of the new session's progression is kept under it and reaches the peer that
    // plays it, which the caller of the older session does not know.
    [Fact]
    public void ALateChangeOfAnOlderSessionThatIsAheadReachesTheNewPeer()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog());
        var owner = fixture.Join(1, "Returning Owner");
        var viewer = fixture.Join(2, "Return Viewer");
        fixture.ShowEveryoneToEveryone();
        var replicator = Create(fixture);
        var current = owner.Context.Session!;
        var older = current with { SimulationSessionId = Guid.NewGuid() };
        replicator.Apply(current, owner.Peer, Change(5, 100, 900, 0, 0, 0), RealtimeProgressionChangeReason.QuestReward);

        replicator.Announce(
            replicator.Keep(older, Change(6, 20, 1000, 1, 820, 1), RealtimeProgressionChangeReason.MobKill),
            null);

        var late = Progressions(owner)[^1];
        Assert.Equal(
            ((ushort)6, 20L, 820L, 1, RealtimeProgressionChangeReason.MobKill),
            (late.Level, late.Experience, late.ExperienceGained, late.LevelsGained, late.Reason));
        Assert.Equal((owner.EntityId, (ushort)6), (LevelUps(viewer).Single().EntityId, LevelUps(viewer).Single().Level));

        // The new session's next change builds on what the late change left.
        replicator.Apply(current, owner.Peer, Change(6, 20, 1000, 1, 0, 0), RealtimeProgressionChangeReason.QuestReward);
        Assert.Equal(2, Progressions(owner).Count);
    }

    [Fact]
    public void ALateChangeOfAnOlderSessionThatIsNotAheadIsDropped()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog());
        var owner = fixture.Join(1, "Ahead Owner");
        var replicator = Create(fixture);
        var current = owner.Context.Session!;
        var older = current with { SimulationSessionId = Guid.NewGuid() };
        replicator.Apply(current, owner.Peer, Change(5, 100, 900, 0, 0, 0), RealtimeProgressionChangeReason.QuestReward);

        Assert.Null(replicator.Keep(older, Change(5, 100, 900, 0, 40, 0), RealtimeProgressionChangeReason.MobKill));
        Assert.Null(replicator.Keep(older, Change(4, 800, 800, 0, 40, 0), RealtimeProgressionChangeReason.MobKill));

        Assert.Single(Progressions(owner));
        Assert.False(replicator.GivesExperience(current.CharacterId, 0));
        Assert.True(replicator.GivesExperience(current.CharacterId, 1));
    }

    // A reconnect of the same session keeps what the worker kept, also when its join ticket was
    // read before a change committed.
    [Fact]
    public void AJoinOfTheSameSessionKeepsTheKeptProgressionAndANewSessionTakesItsTicket()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog());
        var owner = fixture.Join(1, "Rejoining Owner");
        var replicator = Create(fixture);
        var session = owner.Context.Session!;
        replicator.Apply(session, owner.Peer, Change(7, 50, 1200, 2, 300, 1), RealtimeProgressionChangeReason.MobKill);
        owner.Entity.RefreshSession(session with { Progression = new SimulationProgression(6, 900, 1100, 1) });

        replicator.OnPlayerJoined(owner.Context, owner.Entity);

        var snapshot = Progressions(owner)[^1];
        Assert.Equal(
            (RealtimeProgressionChangeReason.Snapshot, (ushort)7, 50L),
            (snapshot.Reason, snapshot.Level, snapshot.Experience));

        replicator.OnPlayerRemoved(owner.Context, session, owner.EntityId);
        replicator.OnPlayerJoined(owner.Context, owner.Entity);
        Assert.Equal((ushort)6, Progressions(owner)[^1].Level);
    }

    // A kept answer that a Mob gives nothing holds for five minutes since it was kept; then one
    // credit goes, and its answer, also one that gained nothing, holds for five minutes again.
    [Fact]
    public void AKeptNoExperienceAnswerExpiresAndAZeroGainAnswerRenewsIt()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog());
        var owner = fixture.Join(1, "Capped Owner");
        var replicator = Create(fixture);
        var session = owner.Context.Session!;
        var window = (int)(ProgressionReplicator.NoExperienceTrust.TotalSeconds * fixture.TickRateHz);
        replicator.Apply(session, owner.Peer, Change(200, 0, 0, int.MaxValue, 0, 0), RealtimeProgressionChangeReason.MobKill);

        Advance(fixture, window - 1);
        Assert.False(replicator.GivesExperience(session.CharacterId, 250));
        Advance(fixture, 1);
        Assert.True(replicator.GivesExperience(session.CharacterId, 250));

        var sent = Progressions(owner).Count;
        replicator.Apply(session, owner.Peer, Change(200, 0, 0, int.MaxValue, 0, 0), RealtimeProgressionChangeReason.MobKill);
        Assert.False(replicator.GivesExperience(session.CharacterId, 250));
        Assert.Equal(sent, Progressions(owner).Count);
        Advance(fixture, window - 1);
        Assert.False(replicator.GivesExperience(session.CharacterId, 250));
    }

    // A kill credit that commits after the player left reaches the body's progression, and not the
    // peer that is still connected while its leave is acknowledged.
    [Fact]
    public async Task ACreditThatCommitsAfterTheLeaveIsKeptAndNotSentToTheLeavingPeer()
    {
        using var fixture = new CombatTickFixture(
            TestRules(),
            TestCatalog(),
            worldActors: WolfNextToTheSpawn(respawnSeconds: 30f),
            quests: true);
        var hunter = fixture.Join(1, "Leaving Hunter");
        var session = hunter.Context.Session!;
        fixture.GameService.OnQuestOperation = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.CommittedProgression(
                request,
                0,
                new SimulationProgressionChange(Guid.Empty, 200, 0, 0, int.MaxValue, 900, 1)));
        var wolf = fixture.WorldActors!.ListActors().First(actor => actor.Definition.Id == "mob.feral_wolf");

        fixture.PlayerCombat.CreditMobKills(wolf, [new MobKillCredit(session.CharacterId, hunter.EntityId)]);
        fixture.Leave(hunter);
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();

        Assert.Single(fixture.GameService.QuestOperations);
        Assert.True(fixture.Core.Peers.TryGetCurrentPeer(hunter.Context, out _), "The leaving peer is still connected.");
        Assert.Empty(Progressions(hunter));
        Assert.False(fixture.Progression.GivesExperience(session.CharacterId, 250));
    }

    private static void Advance(CombatTickFixture fixture, int ticks)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            fixture.Core.Clock.Advance();
        }
    }

    private static ProgressionReplicator Create(CombatTickFixture fixture)
    {
        return new ProgressionReplicator(
            fixture.Core.Peers,
            fixture.Core.Sender,
            fixture.Core.Presence,
            fixture.Entities,
            fixture.Bindings,
            fixture.Core.Clock,
            fixture.TickRateHz);
    }

    private static SimulationProgressionChange Change(
        int level,
        long experience,
        long experienceToNextLevel,
        int highestMobLevelWithoutExperience,
        long experienceGained,
        int levelsGained)
    {
        return new SimulationProgressionChange(
            Guid.Empty,
            level,
            experience,
            experienceToNextLevel,
            highestMobLevelWithoutExperience,
            experienceGained,
            levelsGained);
    }

    private static IReadOnlyList<RealtimeOwnerProgressionChanged> Progressions(CombatTickPlayer player)
    {
        return player.Received<RealtimeOwnerProgressionChanged>(
            RealtimeMessageType.OwnerProgressionChanged,
            RealtimeProtocol.TryDecodeOwnerProgressionChanged);
    }

    private static IReadOnlyList<RealtimeEntityLeveledUp> LevelUps(CombatTickPlayer player)
    {
        return player.Received<RealtimeEntityLeveledUp>(
            RealtimeMessageType.EntityLeveledUp,
            RealtimeProtocol.TryDecodeEntityLeveledUp);
    }
}
