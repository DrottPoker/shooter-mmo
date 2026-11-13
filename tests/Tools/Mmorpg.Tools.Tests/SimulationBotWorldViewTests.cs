using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using static Mmorpg.Tools.Tests.PlayingTestWorld;

namespace Mmorpg.Tools.Tests;

// The world view a playing bot keeps from the messages the worker sends.
public sealed class SimulationBotWorldViewTests
{
    // Recorded messages reach the view through the session, encoded as the worker encodes them.
    [Fact]
    public void TheSessionKeepsActorsMobPositionsCombatAndAdminStateInItsView()
    {
        var bot = new RecordedBot();
        bot.Receive(RealtimeProtocol.EncodeWorldActorSpawn(Mob(100, -130f, -35f)));
        bot.Receive(RealtimeProtocol.EncodeCombatantState(Combatant(100, 1)));
        bot.Receive(RealtimeProtocol.EncodeSimulationSnapshot(new RealtimeSimulationSnapshot(
            1,
            2,
            0,
            1,
            [new RealtimeEntitySnapshot(100, 0, new RealtimePlayerState(-128f, 0.5f, -33f, 0f, 0f, 0f, 0f, true))])));
        bot.Receive(RealtimeProtocol.EncodeOwnerCombatState(OwnCombat(4, health: 80, target: 100, status: RealtimeCombatAttackStatus.Attacking)));
        bot.Receive(RealtimeProtocol.EncodeCombatEventBatch(new RealtimeCombatEventBatch(3,
        [
            new RealtimeCombatEvent(RealtimeCombatEventKind.Hit, OwnEntity, 100, 0, 12, RealtimeCombatDamageType.Normal, 38, RealtimeCombatAttackStyle.Melee)
        ])));
        bot.Receive(RealtimeProtocol.EncodeAdminStateChanged(new RealtimeAdminStateChanged(100, false)));

        var view = bot.View;
        var wolf = Assert.Single(view.Actors).Value;
        Assert.Equal(RealtimeWorldActorKind.Mob, wolf.Kind);
        Assert.Equal("mob.grey_wolf", wolf.ActorDefinitionId);
        Assert.Equal(new SimulationBotPosition(-128f, 0.5f, -33f), wolf.Position);
        Assert.Equal(RealtimeCombatLifeState.Alive, view.Combatants[100].LifeState);
        Assert.Equal(80, view.OwnCombat!.Health);
        Assert.Equal(100ul, view.OwnCombat.TargetEntityId);
        Assert.NotNull(view.AdminState);
        var events = new List<RealtimeCombatEvent>();
        view.DrainCombatEvents(events);
        Assert.Equal(12, Assert.Single(events).Damage);
        Assert.Equal(0, view.QueuedCombatEvents);

        bot.Receive(RealtimeProtocol.EncodeWorldActorDespawn(new RealtimeWorldActorDespawn(100, wolf.RuntimeActorId, "died")));
        Assert.Empty(view.Actors);
        Assert.Empty(view.Combatants);
    }

    [Fact]
    public void AnOlderCombatStateNeverReplacesANewerOne()
    {
        var view = new SimulationBotWorldView(CharacterName);
        view.ApplyCombatantState(Combatant(100, 5, health: 20));
        view.ApplyCombatantState(Combatant(100, 4, health: 50));
        view.ApplyOwnerCombatState(OwnCombat(8, health: 30));
        view.ApplyOwnerCombatState(OwnCombat(7, health: 90));

        Assert.Equal(20, view.Combatants[100].Health);
        Assert.Equal(30, view.OwnCombat!.Health);
    }

    // A presence snapshot arrives in chunks; only a complete, newer snapshot replaces the
    // corpses.
    [Fact]
    public void CorpsesChangeOnlyWithACompleteNewerPresenceSnapshot()
    {
        var view = new SimulationBotWorldView(CharacterName);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        view.ApplyCorpsePresence(Corpses(5, MobCorpse(first, 1f, 1f)));
        Assert.Contains(first, view.Corpses.Keys);

        view.ApplyCorpsePresence(new RealtimeCorpsePresenceSnapshotChunk(6, 0, 2, [MobCorpse(second, 2f, 2f)]));
        Assert.Equal([first], view.Corpses.Keys);
        view.ApplyCorpsePresence(new RealtimeCorpsePresenceSnapshotChunk(6, 1, 2, [PlayerCorpse(first, 1f, 1f)]));
        Assert.Equal(
            new[] { first, second }.Order(),
            view.Corpses.Keys.Order());
        Assert.Equal(CharacterName, view.Corpses[first].Source.CharacterName);

        view.ApplyCorpsePresence(Corpses(4));
        Assert.Equal(2, view.Corpses.Count);
        Assert.Equal(2, view.CorpseSnapshots);
    }

    [Fact]
    public void AnOpenCorpseCarriesTheRevisionOfItsNewestViewUntilItCloses()
    {
        var view = new SimulationBotWorldView(CharacterName);
        var corpse = Guid.NewGuid();

        view.ApplyCorpseInteractionResult(CorpseResult(corpse, RealtimeCorpseInteractionKind.Open));
        Assert.Equal(corpse, view.OpenCorpseId);
        Assert.False(view.OpenCorpseReady);
        view.ApplyCorpseViewState(View(corpse, 4));
        Assert.True(view.OpenCorpseReady);
        Assert.Equal(4, view.OpenCorpseRevision);
        view.ApplyCorpseViewState(View(Guid.NewGuid(), 9));
        Assert.Equal(4, view.OpenCorpseRevision);

        view.ApplyCorpseViewClosed(new RealtimeCorpseViewClosed(corpse, "corpse_emptied", "The corpse is empty."));
        Assert.Equal(Guid.Empty, view.OpenCorpseId);
        Assert.False(view.OpenCorpseReady);
    }

    // The own items follow the owner item messages; a change that skips a revision, such as a
    // death, leaves the view behind the worker until a full state arrives.
    [Fact]
    public void OwnItemsArePatchedFromTheOwnerMessagesAndFallBehindOnAGap()
    {
        var items = new SimulationBotItemView();
        var sword = Carried("weapon.iron_sword", 0);
        var arrows = Worn("ammunition.bronze_arrow", "ammunition", 500);
        items.Load(Inventory(3, sword, arrows));

        Assert.True(items.Apply(new RealtimeOwnerItemStateChanged(
            Guid.NewGuid(),
            3,
            new RealtimeCarryState(4, 100, 3000),
            100,
            [new RealtimeContainerRevision(PermanentInventory, 2)],
            [new RealtimeItemStateChange(sword.ItemInstanceId, RealtimeItemStateLocationKind.Equipment, sword.DefinitionId, 1, 2, Guid.Empty, -1, "main_hand", [], Guid.Empty, 0)],
            [])));
        Assert.Equal("main_hand", items.FindEquipped("main_hand")!.EquipmentSlotId);
        Assert.Null(items.FindCarried("weapon.iron_sword"));
        Assert.True(items.Apply(new RealtimeOwnerItemQuantityChanged(
            RealtimeItemQuantityChangeReason.AmmunitionConsumed,
            4,
            new RealtimeCarryState(5, 95, 3000),
            arrows.ItemInstanceId,
            495,
            2)));
        Assert.Equal(495, items.FindEquipped("ammunition")!.Quantity);
        Assert.False(items.IsBehindServer);

        items.ObserveServerRevision(7);
        Assert.True(items.IsBehindServer);
        Assert.False(items.Apply(new RealtimeOwnerItemQuantityChanged(
            RealtimeItemQuantityChangeReason.AmmunitionConsumed,
            7,
            new RealtimeCarryState(8, 90, 3000),
            arrows.ItemInstanceId,
            490,
            3)));
        Assert.Equal(495, items.FindEquipped("ammunition")!.Quantity);

        items.Load(Inventory(8));
        Assert.False(items.IsBehindServer);
        Assert.Empty(items.Items);
    }

    // The worker drops the inputs of a dead character until it respawns, so the inputs sent
    // while dead, and the ones still waiting when the death arrived, measure the death and not
    // the input path.
    [Fact]
    public void InputsOfADeadCharacterAreNotMeasuredAsAcknowledgementLatency()
    {
        var bot = new RecordedBot();
        bot.SendInputs(2);
        bot.Receive(RealtimeProtocol.EncodeOwnerSimulationSnapshot(OwnerAt(1, 0f, -12f)));
        Assert.Single(bot.Acknowledged);

        bot.SendInputs(1);
        bot.Receive(RealtimeProtocol.EncodeOwnerCombatState(OwnCombat(2, health: 0, lifeState: RealtimeCombatLifeState.Dead)));
        bot.SendInputs(2);
        bot.Receive(RealtimeProtocol.EncodeOwnerCombatState(OwnCombat(3, lifeSequence: 2)));
        bot.SendInputs(1);
        bot.Receive(RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
            2,
            4,
            bot.SentInputs,
            new RealtimePlayerState(0f, 0f, -12f, 0f, 0f, 0f, 0f, true),
            new RealtimeMovementPathProgress(0, 0, 0))));

        Assert.Equal(2, bot.Acknowledged.Count);
    }

    [Fact]
    public async Task ThePlannerReportsTheQueueWaitAndSearchTimeOfEveryPlan()
    {
        var timings = new List<SimulationBotPlanTiming>();
        var bake = CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "development-world-1",
            ChunkSize = 32f,
            Boxes =
            [
                new CollisionBoxAuthoringEntry
                {
                    SourcePath = "Test/Ground#BoxCollider0",
                    LayerMask = CollisionLayers.CharacterMovement,
                    CenterY = -0.25f,
                    HalfExtentX = 15f,
                    HalfExtentY = 0.25f,
                    HalfExtentZ = 15f,
                    RotationW = 1f
                }
            ]
        });
        using var navigation = SimulationBotNavigation.Create(
            new ChunkedStaticCollisionWorld(
                bake.Manifest.WorldId,
                bake.Manifest.Revision,
                bake.Manifest.ChunkSize,
                bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data))),
            1,
            timing =>
            {
                lock (timings)
                {
                    timings.Add(timing);
                }
            });

        var plan = await navigation.TryPlan(Movement, new SimulationVector3(0f, 0f, -8f), new SimulationVector3(0f, 0f, 8f))!;

        Assert.Equal(MovementPathPlanStatus.Reached, plan.Status);
        lock (timings)
        {
            var timing = Assert.Single(timings);
            Assert.Equal(MovementPathPlanStatus.Reached, timing.Status);
            Assert.True(timing.QueueWait >= TimeSpan.Zero);
            Assert.True(timing.SearchTime > TimeSpan.Zero);
        }
    }

    // A joined session with a world view and a transport that keeps nothing.
    private sealed class RecordedBot
    {
        private static readonly Guid Account = Guid.Parse("11111111-2222-4333-8444-555555555555");
        private static readonly Guid Character = Guid.Parse("66666666-7777-4888-8999-aaaaaaaaaaaa");
        private long timestamp = 1_000;

        public RecordedBot()
        {
            View = new SimulationBotWorldView(CharacterName);
            Session = new SimulationBotSession(
                new SimulationBotIdentity(
                    1,
                    Account,
                    Character,
                    CharacterName,
                    "ticket",
                    "local-shard-2",
                    "development-world-2"),
                TimeSpan.FromSeconds(10),
                new StandStill(),
                latency => Acknowledged.Add(latency),
                world: View);
            Session.Start(timestamp);
            Session.OnConnected(new NullTransport());
            Join();
        }

        public SimulationBotWorldView View { get; }

        public SimulationBotSession Session { get; }

        public List<double> Acknowledged { get; } = [];

        public uint SentInputs { get; private set; }

        // One movement input per tick, as the session sends them.
        public void SendInputs(int count)
        {
            var interval = (long)Math.Ceiling(Stopwatch.Frequency / 30d);
            for (var input = 0; input < count; input++)
            {
                Session.Update(timestamp);
                timestamp += interval;
                SentInputs++;
            }
        }

        public void Receive(byte[] packet)
        {
            Assert.True(RealtimeProtocol.TryReadMessageType(packet, out var type));
            Assert.True(RealtimeProtocol.TryGetDescriptor(type, out var descriptor));
            Session.Receive(packet, descriptor.Channel, descriptor.Delivery, ++timestamp);
            Assert.Equal(SimulationBotClientState.Joined, Session.State);
        }

        private void Join()
        {
            var accepted = PlayingTestWorld.Join();
            var identityAccepted = new RealtimeJoinAccepted(
                accepted.SimulationSessionId,
                Account.ToString("D"),
                Character.ToString("D"),
                CharacterName,
                "local-shard-2",
                "development-world-2",
                OwnEntity,
                accepted.SimulationRevision,
                accepted.CollisionRevision,
                accepted.JoinedAt,
                accepted.SessionExpiresAt,
                false,
                accepted.CarryState,
                accepted.MovementSettings,
                accepted.InitialPlayerState,
                accepted.ShardRules);
            var packet = RealtimeProtocol.EncodeJoinAccepted(identityAccepted);
            Assert.True(RealtimeProtocol.TryGetDescriptor(RealtimeMessageType.JoinAccepted, out var descriptor));
            Session.Receive(packet, descriptor.Channel, descriptor.Delivery, ++timestamp);
        }

    }

    private sealed class StandStill : ISimulationBotInputSource
    {
        public RealtimeMovementPath? SelectPath(uint clientTick, RealtimeJoinAccepted joinedSession, SimulationBotPosition position) => null;
    }

    private sealed class NullTransport : ISimulationBotTransport
    {
        public void Send(byte[] packet, byte channel, RealtimeDelivery delivery)
        {
        }
    }
}
