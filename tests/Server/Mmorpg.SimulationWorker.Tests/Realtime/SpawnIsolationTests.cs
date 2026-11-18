using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A spawn that cannot be built costs its entity for the connection that it was built for: it is
// logged and counted, the connection does not see the entity at all, and the interest refresh goes
// on with every other entity and every other connection. Without that, the exception left the
// refresh with the visible sets already updated, so the entities after the failing one were
// visible and never spawned, and the phase failed again for every connection that saw the entity.
public sealed class SpawnIsolationTests
{
    private const string SpawnFailures = "simulation_worker.realtime.spawn.failures";

    [Fact]
    public void APlayerSpawnThatCannotBeBuiltCostsThatPlayerForEveryConnectionAndNothingElse()
    {
        using var listening = Listen(out var failures, out var created);
        created.Value = true;
        using var fixture = new RealtimeLoopFixture();
        created.Value = false;
        var first = fixture.ConnectPlayer(1, "Isolation First");
        var second = fixture.ConnectPlayer(2, "Isolation Second");
        var broken = AddBodyWithoutPeer(fixture, new string('P', RealtimeProtocolLimits.MaximumNameLength + 1));
        var last = fixture.ConnectPlayer(4, "Isolation Last");
        var viewers = new[] { first, second, last };
        Assert.True(broken.NetworkEntityId > second.Entity.NetworkEntityId);
        Assert.True(broken.NetworkEntityId < last.Entity.NetworkEntityId);

        // The interest phase refreshes every connection; none of it fails.
        var interest = new InterestRefresher(fixture.Entities, fixture.Presence, fixture.PerformanceMetrics);
        for (var refresh = 0; refresh < fixture.Config.InterestManagement.RefreshIntervalSnapshots; refresh++)
        {
            interest.Refresh();
        }

        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
        var everyoneButTheBody = viewers.Select(viewer => viewer.Entity.NetworkEntityId).Order().ToArray();
        foreach (var viewer in viewers)
        {
            // The entities before and after the failing one all spawned, and the visible set holds
            // exactly them: the viewer is not told of the entity that has no spawn.
            Assert.Equal(everyoneButTheBody, SpawnedEntities(viewer).Order());
            Assert.Equal(everyoneButTheBody, fixture.Presence.GetVisibleOrdered(viewer.Context.PeerId));
            Assert.False(fixture.Presence.IsVisibleTo(viewer.Context.PeerId, broken.NetworkEntityId));
        }

        // Each connection logged and counted its own failure, once, with what it needs to find it.
        listening.RecordObservableInstruments();
        Assert.Equal(3, failures.GetValueOrDefault(SpawnFailures));
        var errors = fixture.Logger.Entries.Where(entry => entry.Level == LogLevel.Error).ToArray();
        Assert.Equal(3, errors.Length);
        Assert.All(errors, error =>
        {
            Assert.Contains($"spawn of entity {broken.NetworkEntityId} could not be built", error.Message, StringComparison.Ordinal);
            Assert.IsType<ArgumentException>(error.Exception);
        });
        foreach (var viewer in viewers)
        {
            Assert.Single(errors, error => error.Message.Contains($"UDP peer {viewer.Context.PeerId},", StringComparison.Ordinal));
        }

        // It is not tried again at every refresh while it stays in range, and nothing about it
        // reaches a connection that never got it: no change, and no despawn when it leaves.
        for (var refresh = 0; refresh < 2 * fixture.Config.InterestManagement.RefreshIntervalSnapshots; refresh++)
        {
            interest.Refresh();
        }

        var recipients = new List<int>();
        fixture.Presence.CollectChangeRecipients(broken.NetworkEntityId, MissedStates.Combatant, true, -1, recipients);
        Assert.Empty(recipients);
        fixture.Presence.BroadcastEntityDespawn(broken.NetworkEntityId, "left_shard", null);
        listening.RecordObservableInstruments();
        Assert.Equal(3, failures.GetValueOrDefault(SpawnFailures));
        Assert.Equal(3, fixture.Logger.Entries.Count(entry => entry.Level == LogLevel.Error));
        foreach (var viewer in viewers)
        {
            Assert.Equal(everyoneButTheBody, SpawnedEntities(viewer).Order());
            Assert.DoesNotContain(viewer.Peer.Sent, sent => sent.Type == RealtimeMessageType.EntityDespawn);
        }

        // The player of the connection that cannot spawn its own entity is the join's concern,
        // which fails as it did before: the exception is not swallowed there.
        var poisonedPlayer = fixture.ConnectPlayer(5, new string('Q', RealtimeProtocolLimits.MaximumNameLength + 1));
        Assert.Throws<ArgumentException>(() => fixture.Presence.SendEntityBaseline(
            poisonedPlayer.Peer,
            new HashSet<ulong> { poisonedPlayer.Entity.NetworkEntityId }));
    }

    // A world actor's spawn is isolated the same way: the viewer sees every actor around it
    // but the one whose spawn cannot be built.
    [Fact]
    public void AWorldActorSpawnThatCannotBeBuiltCostsOnlyThatActor()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var actors = fixture.WorldActors!.ListActors().OrderBy(actor => actor.NetworkEntityId).ToArray();
        var viewer = fixture.ConnectPlayer(1, "Actor Viewer");
        viewer.Entity.Movement.Teleport(new PlayerMovementState(actors[0].PositionX, 0f, actors[0].PositionZ, 0f, 0f, 0f, 0f, true));
        var inRange = actors
            .Where(actor => Math.Abs(actor.PositionX - actors[0].PositionX) < 100f && Math.Abs(actor.PositionZ - actors[0].PositionZ) < 100f)
            .ToArray();
        Assert.True(inRange.Length >= 3, "The test World needs three actors around one place.");
        var broken = inRange[1];
        fixture.Presence.IsActorMoving = actor => actor == broken
            ? throw new InvalidOperationException("The pose of the actor broke.")
            : false;

        var interest = new InterestRefresher(fixture.Entities, fixture.Presence, fixture.PerformanceMetrics);
        for (var refresh = 0; refresh < fixture.Config.InterestManagement.RefreshIntervalSnapshots; refresh++)
        {
            interest.Refresh();
        }

        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
        var spawned = viewer.Peer.Sent
            .Where(sent => sent.Type == RealtimeMessageType.WorldActorSpawn)
            .Select(sent =>
            {
                Assert.True(RealtimeProtocol.TryDecodeWorldActorSpawn(sent.Data, out var spawn, out var error), error);
                return spawn.EntityId;
            })
            .ToArray();
        Assert.DoesNotContain(broken.NetworkEntityId, spawned);
        Assert.False(fixture.Presence.IsVisibleTo(viewer.Context.PeerId, broken.NetworkEntityId));
        Assert.All(inRange.Where(actor => actor != broken), actor =>
        {
            Assert.Contains(actor.NetworkEntityId, spawned);
            Assert.True(fixture.Presence.IsVisibleTo(viewer.Context.PeerId, actor.NetworkEntityId));
        });
        var error = Assert.Single(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains($"spawn of entity {broken.NetworkEntityId} could not be built", error.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    private static List<ulong> SpawnedEntities(ConnectedPlayer viewer)
    {
        return viewer.Peer.Sent
            .Where(sent => sent.Type == RealtimeMessageType.EntitySpawn)
            .Select(sent =>
            {
                Assert.True(RealtimeProtocol.TryDecodeEntitySpawn(sent.Data, out var spawn, out var error), error);
                return spawn.EntityId;
            })
            .ToList();
    }

    // A player entity that no connection controls, as a lingering body is, whose character name the
    // spawn packet cannot carry.
    private static PlayerSimulationEntity AddBodyWithoutPeer(RealtimeLoopFixture fixture, string name)
    {
        var session = RealtimeTestSessions.Create(name);
        var initialState = PlayerMovementSimulation.CreateInitialState(
            fixture.Config.MovementSimulation,
            fixture.CollisionWorld,
            fixture.Config.MovementSpawn.X,
            fixture.Config.MovementSpawn.Y,
            fixture.Config.MovementSpawn.Z,
            fixture.Config.MovementSpawn.YawDegrees);
        return fixture.Entities.RegisterPlayer(
                session,
                () => new AuthoritativePlayerMovement(
                    initialState,
                    fixture.Config.MovementSimulation,
                    session.CarryState,
                    fixture.CollisionWorld))
            .Entity;
    }

    // Listens to the spawn failure counter of the metrics that this thread creates while the flag
    // is set, since other tests create metrics under the same meter name at the same time.
    private static MeterListener Listen(out Dictionary<string, long> measured, out ThreadFlag created)
    {
        var flag = new ThreadFlag(Environment.CurrentManagedThreadId);
        var values = new Dictionary<string, long>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (flag.IsOnItsThread && flag.Value && instrument.Name == SpawnFailures)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => values[instrument.Name] = value);
        listener.Start();
        measured = values;
        created = flag;
        return listener;
    }

    private sealed class ThreadFlag(int thread)
    {
        public bool Value { get; set; }

        public bool IsOnItsThread => Environment.CurrentManagedThreadId == thread;
    }
}
