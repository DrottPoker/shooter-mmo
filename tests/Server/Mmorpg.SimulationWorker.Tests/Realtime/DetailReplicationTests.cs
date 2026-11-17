using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Combat;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// What a viewer gets of a fight within the detail radius, 70 m, and beyond it: within it every
// state and event at once; beyond it no attack or hit and the newest state once on the
// entity's slot, but a death at once; and everything at once again when the fight comes within
// 70 m or when the viewer is one side of it. The real combat system runs tick by tick around
// recording peers.
public sealed class DetailReplicationTests
{
    private const string Weapon = "test.detail_weapon";
    private static readonly Guid WeaponItemId = Guid.Parse("de7a1100-0000-4000-8000-000000000001");

    [Fact]
    public void AViewerAt60MetresGetsEveryChangeAndOneAt90MetresTheNewestStateOnTheSlot()
    {
        using var fixture = new CombatTickFixture(RealtimeCombatTests.TestRules(), Catalog(damage: 15, range: 3f));
        var attacker = fixture.Join(1, "Detail Attacker", Armed());
        var target = fixture.Join(2, "Detail Target");
        var near = fixture.Join(3, "Detail Near Viewer");
        var far = fixture.Join(4, "Detail Far Viewer");
        Place(attacker, 1.5f, 0f);
        Place(target, 0f, 0f);
        Place(near, 60f, 0f);
        Place(far, -90f, 0f);
        ShowEveryoneWhereTheyStand(fixture);
        var nearStates = near.StatesOf(target.EntityId).Count;
        var farStates = far.StatesOf(target.EntityId).Count;

        Attack(fixture, attacker, target);
        RunUntil(fixture, () => fixture.CombatantOf(target).Health <= 100 - (3 * 15), 5 * fixture.TickRateHz);
        fixture.SendCombatIntent(attacker, RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()));
        fixture.RunTick();

        // Within 70 m every attack, hit, and state arrives; beyond it none of them.
        Assert.True(Events(near, RealtimeCombatEventKind.Hit) >= 3);
        Assert.True(Events(near, RealtimeCombatEventKind.AttackStarted) >= 3);
        Assert.True(Events(near, RealtimeCombatEventKind.AttackReleased) >= 3);
        Assert.True(near.StatesOf(target.EntityId).Count >= nearStates + 3);
        Assert.Empty(far.Received<RealtimeCombatEventBatch>(RealtimeMessageType.CombatEventBatch, RealtimeProtocol.TryDecodeCombatEventBatch));
        Assert.Equal(farStates, far.StatesOf(target.EntityId).Count);

        // On the target's slot the far viewer gets the newest state once, and the near one
        // nothing more; the attacker's state waits for the attacker's slot.
        var refresher = FarStates(fixture);
        var slots = (uint)fixture.Config.SnapshotRateHz;
        Assert.NotEqual(target.EntityId % slots, attacker.EntityId % slots);
        var nearReceived = near.Peer.Sent.Count;
        var farAttackerStates = far.StatesOf(attacker.EntityId).Count;
        refresher.Send((uint)(target.EntityId % slots));
        var newest = Assert.Single(far.StatesOf(target.EntityId).Skip(farStates));
        var targetState = fixture.CombatantOf(target).ToState(fixture.ServerTick, 0);
        Assert.Equal((targetState.StateRevision, targetState.Health), (newest.StateRevision, newest.Health));
        Assert.Equal(farAttackerStates, far.StatesOf(attacker.EntityId).Count);
        refresher.Send((uint)(target.EntityId % slots));
        Assert.Equal(farStates + 1, far.StatesOf(target.EntityId).Count);
        for (var phase = 0u; phase < slots; phase++)
        {
            refresher.Send(phase);
        }

        Assert.Equal(farAttackerStates + 1, far.StatesOf(attacker.EntityId).Count);
        Assert.Equal(fixture.CombatantOf(attacker).StateRevision, far.StatesOf(attacker.EntityId)[^1].StateRevision);
        Assert.Equal(nearReceived, near.Peer.Sent.Count);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    [Fact]
    public void ADeathReachesTheViewersAt60And90MetresAtOnce()
    {
        using var fixture = new CombatTickFixture(RealtimeCombatTests.TestRules(respawnSeconds: 30f), Catalog(damage: 500, range: 3f));
        var attacker = fixture.Join(1, "Lethal Attacker", Armed());
        var target = fixture.Join(2, "Lethal Target");
        var near = fixture.Join(3, "Lethal Near Viewer");
        var far = fixture.Join(4, "Lethal Far Viewer");
        Place(attacker, 1.5f, 0f);
        Place(target, 0f, 0f);
        Place(near, 60f, 0f);
        Place(far, -90f, 0f);
        ShowEveryoneWhereTheyStand(fixture);

        Attack(fixture, attacker, target);
        RunUntil(fixture, () => !fixture.CombatantOf(target).IsAlive, fixture.TickRateHz);

        foreach (var viewer in new[] { near, far })
        {
            Assert.Equal(1, Events(viewer, RealtimeCombatEventKind.Died));
            Assert.Equal(RealtimeCombatLifeState.Dead, viewer.StatesOf(target.EntityId)[^1].LifeState);
        }

        Assert.Equal(1, Events(near, RealtimeCombatEventKind.Hit));
        Assert.Equal(0, Events(far, RealtimeCombatEventKind.Hit));
        Assert.Equal(0, Events(far, RealtimeCombatEventKind.AttackStarted));
    }

    [Fact]
    public void WalkingFrom90To60MetresBringsTheMissedStatesAtOnce()
    {
        using var fixture = new CombatTickFixture(RealtimeCombatTests.TestRules(), Catalog(damage: 15, range: 3f));
        var attacker = fixture.Join(1, "Approached Attacker", Armed());
        var target = fixture.Join(2, "Approached Target");
        var walker = fixture.Join(3, "Approaching Viewer");
        Place(attacker, 1.5f, 0f);
        Place(target, 0f, 0f);
        Place(walker, -90f, 0f);
        ShowEveryoneWhereTheyStand(fixture);
        var states = walker.StatesOf(target.EntityId).Count;

        Attack(fixture, attacker, target);
        RunUntil(fixture, () => fixture.CombatantOf(target).Health < 100, 2 * fixture.TickRateHz);
        Assert.Equal(states, walker.StatesOf(target.EntityId).Count);

        // The viewer comes within 70 m; its next refresh brings the newest state of the fight's
        // two sides before any slot, and from then on it gets every hit.
        Place(walker, -60f, 0f);
        RefreshEveryone(fixture);
        var caughtUp = walker.StatesOf(target.EntityId);
        Assert.Equal(states + 1, caughtUp.Count);
        Assert.Equal(fixture.CombatantOf(target).StateRevision, caughtUp[^1].StateRevision);
        Assert.Equal(fixture.CombatantOf(attacker).StateRevision, walker.StatesOf(attacker.EntityId)[^1].StateRevision);
        var health = fixture.CombatantOf(target).Health;
        RunUntil(fixture, () => fixture.CombatantOf(target).Health < health, 2 * fixture.TickRateHz);
        Assert.True(Events(walker, RealtimeCombatEventKind.Hit) >= 1);
        Assert.Equal(fixture.CombatantOf(target).StateRevision, walker.StatesOf(target.EntityId)[^1].StateRevision);
    }

    [Fact]
    public void TheTwoSidesOfAFightAcross100MetresGetEverything()
    {
        using var fixture = new CombatTickFixture(RealtimeCombatTests.TestRules(), Catalog(damage: 5, range: 150f));
        var archer = fixture.Join(1, "Distant Archer", Armed());
        var target = fixture.Join(2, "Distant Target", Armed());
        Place(archer, 0f, 0f);
        Place(target, 100f, 0f);
        ShowEveryoneWhereTheyStand(fixture);

        // A state the archer misses while it does not target the far player yet.
        Assert.True(fixture.Combat.TryRestoreVitals(fixture.CombatantOf(target), 90, 100));
        fixture.RunTick();
        var missed = archer.StatesOf(target.EntityId).Count;
        Assert.NotEqual(fixture.CombatantOf(target).StateRevision, archer.StatesOf(target.EntityId)[^1].StateRevision);

        // Targeting brings it at once, and both sides get every event and state of the fight.
        Attack(fixture, archer, target);
        Attack(fixture, target, archer);
        fixture.RunTick();
        Assert.True(archer.StatesOf(target.EntityId).Count > missed);
        RunUntil(
            fixture,
            () => fixture.CombatantOf(target).Health <= 90 - (3 * 5) && fixture.CombatantOf(archer).Health <= 100 - (3 * 5),
            6 * fixture.TickRateHz);
        foreach (var (side, other) in new[] { (archer, target), (target, archer) })
        {
            Assert.True(Events(side, RealtimeCombatEventKind.Hit) >= 6);
            Assert.True(Events(side, RealtimeCombatEventKind.AttackStarted) >= 6);
            Assert.Equal(fixture.CombatantOf(other).StateRevision, side.StatesOf(other.EntityId)[^1].StateRevision);
            Assert.Equal(fixture.CombatantOf(other).Health, side.StatesOf(other.EntityId)[^1].Health);
        }
    }

    // A Mob's rest pose reaches a viewer at 60 m at once and one at 90 m on the Mob's slot, but
    // not while the Mob walks again, since a client takes it as the pose the Mob rests at; a Mob
    // that becomes hidden reaches both at once.
    [Fact]
    public void AHiddenMobReachesEveryViewerAtOnceAndAFarRestPoseWaitsWhileTheMobWalks()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var mob = fixture.WorldActors!.ListActors().First(actor => actor.Definition.Kind == WorldActorKindIds.Mob);
        var near = fixture.ConnectPlayer(1, "Mob Watcher At 60 Metres");
        var far = fixture.ConnectPlayer(2, "Mob Watcher At 90 Metres");
        near.Entity.Movement.Teleport(new PlayerMovementState(mob.PositionX + 60f, 0f, mob.PositionZ, 0f, 0f, 0f, 0f, true));
        far.Entity.Movement.Teleport(new PlayerMovementState(mob.PositionX - 90f, 0f, mob.PositionZ, 0f, 0f, 0f, 0f, true));
        var players = new List<PlayerSimulationEntity>();
        fixture.Entities.CopyPlayersTo(players);
        fixture.Presence.SynchronizeInterestIndex(players);
        for (var refresh = 0; refresh < fixture.Config.InterestManagement.RefreshIntervalSnapshots; refresh++)
        {
            fixture.Presence.RefreshInterests();
        }

        Assert.True(fixture.Presence.IsVisibleTo(far.Context.PeerId, mob.NetworkEntityId));
        var walking = false;
        fixture.Presence.IsActorMoving = _ => walking;
        var refresher = new FarStateRefresher(
            fixture.Presence,
            fixture.Registry,
            fixture.Sender,
            fixture.FailureIsolation,
            fixture.Config.SnapshotRateHz);
        var slot = (uint)(mob.NetworkEntityId % (uint)fixture.Config.SnapshotRateHz);

        mob.PublishRestPose();
        fixture.Presence.BroadcastWorldActorState(mob);
        Assert.Equal(mob.StateRevision, Assert.Single(StatesOf(near, mob)).StateRevision);
        Assert.Empty(StatesOf(far, mob));

        // The Mob walks on before its slot: the far viewer keeps waiting, and gets the state once
        // the Mob stands.
        walking = true;
        refresher.Send(slot);
        Assert.Empty(StatesOf(far, mob));
        walking = false;
        refresher.Send(slot);
        Assert.Equal(mob.StateRevision, Assert.Single(StatesOf(far, mob)).StateRevision);

        // The Mob hides: both viewers get it at once.
        Assert.True(mob.SetActive(false));
        fixture.Presence.BroadcastWorldActorState(mob);
        Assert.False(StatesOf(near, mob)[^1].IsActive);
        Assert.False(StatesOf(far, mob)[^1].IsActive);
        Assert.Equal(2, StatesOf(far, mob).Count);
    }

    private static IReadOnlyList<RealtimeWorldActorState> StatesOf(ConnectedPlayer viewer, WorldActorRuntimeState actor)
    {
        return viewer.Peer.Sent
            .Where(sent => sent.Type == RealtimeMessageType.WorldActorState)
            .Select(sent =>
            {
                Assert.True(RealtimeProtocol.TryDecodeWorldActorState(sent.Data, out var state, out var error), error);
                return state;
            })
            .Where(state => state.EntityId == actor.NetworkEntityId)
            .ToArray();
    }

    private static SimulationEquippedItem Armed()
    {
        return new SimulationEquippedItem("main_hand", WeaponItemId, Weapon, 1, 1);
    }

    // Stands the player at the place; the fixture moves nobody, so the place holds.
    private static void Place(CombatTickPlayer player, float x, float z)
    {
        player.Entity.Movement.Teleport(new PlayerMovementState(x, 0f, z, 0f, 0f, 0f, 0f, true));
    }

    private static void Attack(CombatTickFixture fixture, CombatTickPlayer attacker, CombatTickPlayer target)
    {
        fixture.SendCombatIntent(attacker, RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), target.EntityId));
    }

    private static void RunUntil(CombatTickFixture fixture, Func<bool> condition, int maximumTicks)
    {
        for (var tick = 0; tick < maximumTicks && !condition(); tick++)
        {
            fixture.RunTick();
        }

        Assert.True(condition(), $"Waited {maximumTicks} ticks.");
    }

    // Brings the interest index to the players' places, which the joins indexed at the spawn,
    // and shows every player what it sees from there.
    private static void ShowEveryoneWhereTheyStand(CombatTickFixture fixture)
    {
        SynchronizeIndex(fixture);
        fixture.ShowEveryoneToEveryone();
    }

    private static void SynchronizeIndex(CombatTickFixture fixture)
    {
        var players = new List<PlayerSimulationEntity>();
        fixture.Entities.CopyPlayersTo(players);
        fixture.Core.Presence.SynchronizeInterestIndex(players);
    }

    // Brings the interest index to the players' places and refreshes every connection.
    private static void RefreshEveryone(CombatTickFixture fixture)
    {
        SynchronizeIndex(fixture);
        for (var refresh = 0; refresh < fixture.Config.InterestManagement.RefreshIntervalSnapshots; refresh++)
        {
            fixture.Core.Presence.RefreshInterests();
        }
    }

    private static FarStateRefresher FarStates(CombatTickFixture fixture)
    {
        return new FarStateRefresher(
            fixture.Core.Presence,
            fixture.Core.Peers,
            fixture.Core.Sender,
            fixture.Core.FailureIsolation,
            fixture.Config.SnapshotRateHz);
    }

    private static int Events(CombatTickPlayer player, RealtimeCombatEventKind kind)
    {
        return player.Received<RealtimeCombatEventBatch>(RealtimeMessageType.CombatEventBatch, RealtimeProtocol.TryDecodeCombatEventBatch)
            .SelectMany(batch => batch.Events)
            .Count(combatEvent => combatEvent.Kind == kind);
    }

    private static ItemCatalogRuntimeDocument Catalog(int damage, float range)
    {
        return new ItemCatalogRuntimeDocument
        {
            WeaponFamilies =
            [
                new ItemWeaponFamilyEntry
                {
                    Id = "sword",
                    CombatStyle = CombatStyleIds.Melee,
                    AmmunitionFamily = string.Empty,
                    ProjectileSpeed = 0f,
                    WindupSeconds = 0f
                }
            ],
            Definitions =
            [
                new ItemDefinition
                {
                    Id = Weapon,
                    Category = ItemCategoryIds.Weapon,
                    Weapon = new ItemWeaponEntry
                    {
                        WeaponFamily = "sword",
                        DamageType = DamageTypeIds.Normal,
                        MinimumDamage = damage,
                        MaximumDamage = damage,
                        AttackIntervalSeconds = 0.5f,
                        Range = range,
                        ManaCost = 0
                    }
                }
            ]
        };
    }
}
