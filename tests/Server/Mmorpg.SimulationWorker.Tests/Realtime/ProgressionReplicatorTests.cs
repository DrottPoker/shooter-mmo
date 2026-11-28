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

    private static ProgressionReplicator Create(CombatTickFixture fixture)
    {
        return new ProgressionReplicator(
            fixture.Core.Peers,
            fixture.Core.Sender,
            fixture.Core.Presence,
            fixture.Entities,
            fixture.Bindings);
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
