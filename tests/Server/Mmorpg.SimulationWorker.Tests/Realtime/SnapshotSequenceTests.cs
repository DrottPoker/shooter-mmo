using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The snapshot sequence of a broadcast, which a failed broadcast must not leave behind for the next
// one, and which wraps around after 2^32 snapshots, as the server tick does, in the replicator,
// the records, the scheduler, and the far states.
public sealed class SnapshotSequenceTests
{
    private const int RefreshInterval = 15;
    private const int FarInterval = 3;

    // A source of Mob states that throws once, after the players of the broadcast reported their
    // records under the sequence of the broadcast, as any later step of it might.
    private sealed class ThrowsOnceMobSource : IMobSnapshotSource
    {
        public bool Thrown { get; private set; }

        public void CopyStatesTo(List<SnapshotEntityState> destination)
        {
            destination.Clear();
            if (!Thrown)
            {
                Thrown = true;
                throw new InvalidOperationException("The Mob states broke.");
            }
        }
    }

    private sealed class NoMobSource : IMobSnapshotSource
    {
        public void CopyStatesTo(List<SnapshotEntityState> destination)
        {
            destination.Clear();
        }
    }

    // A broadcast that fails after its players reported leaves the next one working: it takes
    // the next sequence, where it used to take the one of the failed broadcast and fail on every
    // entity with "already has a snapshot record", on every run until the worker stopped.
    [Fact]
    public void ABroadcastThatFailedAfterItsFirstReportsNeverPoisonsTheNextOne()
    {
        using var fixture = new RealtimeLoopFixture();
        var first = fixture.ConnectPlayer(1, "Sequence First");
        var second = fixture.ConnectPlayer(2, "Sequence Second");
        var mobs = new ThrowsOnceMobSource();
        var replicator = CreateReplicator(fixture, mobs);
        ShowEachOther(fixture);

        var failure = Assert.Throws<InvalidOperationException>(replicator.Broadcast);
        Assert.Equal("The Mob states broke.", failure.Message);
        Assert.True(mobs.Thrown);
        Assert.Empty(OwnerSequences(first));

        for (var broadcast = 0; broadcast < 3; broadcast++)
        {
            replicator.Broadcast();
        }

        // The failed broadcast took sequence 1, so the next ones take 2, 3, and 4.
        Assert.Equal([2u, 3u, 4u], OwnerSequences(first));
        Assert.Equal([2u, 3u, 4u], OwnerSequences(second));
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // The sequence runs through 2^32 - 1 and on to 0 in the broadcasts, and a remote record that
    // is due once, such as the first of an entity in view, is not sent again by the wrap.
    [Fact]
    public void TheBroadcastsCrossTheWrapOfTheSnapshotSequence()
    {
        using var fixture = new RealtimeLoopFixture();
        var first = fixture.ConnectPlayer(1, "Wrap First");
        var second = fixture.ConnectPlayer(2, "Wrap Second");
        var replicator = CreateReplicator(fixture, new NoMobSource(), uint.MaxValue - 2);
        ShowEachOther(fixture);

        for (var broadcast = 0; broadcast < 6; broadcast++)
        {
            replicator.Broadcast();
        }

        uint[] expected = [uint.MaxValue - 1, uint.MaxValue, 0u, 1u, 2u, 3u];
        Assert.Equal(expected, OwnerSequences(first));
        Assert.Equal(expected, OwnerSequences(second));

        // The other player is new in view in the first remote snapshot and not due again for a
        // refresh interval, which the wrap does not shorten.
        foreach (var viewer in new[] { first, second })
        {
            var remote = viewer.Peer.Sent
                .Where(sent => sent.Type == RealtimeMessageType.SimulationSnapshot)
                .Select(sent =>
                {
                    Assert.True(RealtimeProtocol.TryDecodeSimulationSnapshot(sent.Data, out var snapshot, out var error), error);
                    return snapshot;
                })
                .ToArray();
            var snapshot = Assert.Single(remote);
            Assert.Equal(uint.MaxValue - 1, snapshot.SnapshotSequence);
        }

        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // The records and the scheduler through the wrap: an entity that moves in every snapshot has
    // a record in every snapshot, a far one that moves one at least every far interval, and one
    // that stands is refreshed within the refresh interval; and a start from a stand is a start
    // also when its snapshot is the first after the wrap, while a walk that goes on across the wrap
    // is none.
    [Fact]
    public void TheRecordsAndTheSchedulerKeepTheirCadenceThroughTheWrap()
    {
        const uint First = uint.MaxValue - 40;
        const int WrapIndex = 41;
        const int Snapshots = 90;
        var visible = new ulong[] { 10, 11, 12, 13, 14 };

        // 10 stands far away, 11 walks near, 12 walks far, 13 stands until the first snapshot
        // after the wrap and then walks far, and 14 walks near across the wrap.
        var near = new[] { false, true, false, false, true };
        var records = new SnapshotRecords();
        var scheduler = new SnapshotScheduler(SnapshotReplicationConfig.Default);
        var state = new SnapshotRecipientState(new RealtimePeerContext(1, DateTime.UtcNow, UdpQuotaConfig.Default), ulong.MaxValue);
        var received = visible.ToDictionary(entityId => entityId, _ => new List<uint>());
        var starts = visible.ToDictionary(entityId => entityId, _ => new Dictionary<uint, bool>());
        for (var index = 0; index < Snapshots; index++)
        {
            var sequence = unchecked(First + (uint)index);
            records.BeginSnapshot(sequence);
            foreach (var entityId in visible)
            {
                var walked = entityId switch
                {
                    10 => 0f,
                    13 => index < WrapIndex ? 0f : (index - WrapIndex + 1) * 0.2f,
                    _ => index * 0.2f
                };
                Assert.True(records.Report(entityId, new PlayerMovementState((entityId * 3f) + walked, 0f, 0f, 0f, 0f, 0f, 0f, true)));
            }

            records.EndReports();
            foreach (var entityId in visible)
            {
                Assert.True(records.TryGetSlot(entityId, out var slot));
                starts[entityId][sequence] = records.Starts[slot];
            }

            var plan = scheduler.Plan(records, state, visible, near, 100_000);
            for (var chunk = 0; chunk < plan.PacketCount; chunk++)
            {
                Assert.True(
                    RealtimeProtocol.TryDecodeSimulationSnapshot(
                        scheduler.WriteChunk(chunk, sequence, out _).ToArray(),
                        out var snapshot,
                        out var error),
                    error);
                Assert.Equal(sequence, snapshot.SnapshotSequence);
                foreach (var entity in snapshot.Entities)
                {
                    received[entity.EntityId].Add(sequence);
                }
            }

            scheduler.Complete(sent: true);
        }

        // Everyone is new in view in the first snapshot.
        Assert.All(received.Values, sequences => Assert.Equal(First, sequences[0]));

        // A near walker has a record in every snapshot, the last before the wrap and the first
        // after it included.
        foreach (var walker in new ulong[] { 11, 14 })
        {
            Assert.Equal(Snapshots, received[walker].Count);
            Assert.Contains(uint.MaxValue, received[walker]);
            Assert.Contains(0u, received[walker]);
        }

        // A far walker has one at least every far interval and a stander at least every refresh
        // interval. The slot of an entity comes from the sequence modulo the interval, and 2^32 is
        // no multiple of 3 or 15, so the wrap puts every slot one snapshot, 67 ms, later once:
        // no entity waits more than that one snapshot longer, and none is skipped.
        Assert.True(MaximumGap(received[12]) <= FarInterval, $"A far walker waited {MaximumGap(received[12])} snapshots.");
        Assert.True(MaximumGap(received[10]) <= RefreshInterval + 1, $"A stander waited {MaximumGap(received[10])} snapshots.");
        Assert.InRange(received[10].Count, Snapshots / RefreshInterval, (Snapshots / RefreshInterval) * 2);

        // The walker that stood until the wrap is in its start in the snapshots from the wrap on,
        // and a walk that goes on across it never is.
        Assert.False(starts[13][uint.MaxValue]);
        Assert.True(starts[13][0]);
        Assert.True(starts[13][1]);
        Assert.False(starts[13][2]);
        Assert.All(new ulong[] { 11, 12, 14 }, walker => Assert.DoesNotContain(true, starts[walker].Values));

        // Its two snapshots of the start carry its records at once, not on a far slot.
        Assert.Contains(0u, received[13]);
        Assert.Contains(1u, received[13]);
    }

    // The far states through the wrap of the server tick, which sends every entity's missed
    // state on its slot once a second. The slot comes from the tick, which wraps in a number of
    // snapshots that 15 does not divide, so one wrap skips some slots once; no state waits longer
    // than two seconds, whichever tick of the second the wrap falls on.
    [Fact]
    public void TheFarStatesReachEveryEntityWithinTwoSecondsWhereverTheTickWraps()
    {
        using var fixture = new RealtimeLoopFixture();
        const int Others = 31;
        var viewer = fixture.ConnectPlayer(1, "Far Viewer");
        var others = Enumerable.Range(0, Others)
            .Select(index => fixture.ConnectPlayer(2 + index, $"Far Entity {index}"))
            .ToArray();
        var players = new List<Mmorpg.SimulationWorker.Entities.PlayerSimulationEntity>();
        fixture.Entities.CopyPlayersTo(players);
        fixture.Presence.SynchronizeInterestIndex(players);
        for (var refresh = 0; refresh < fixture.Config.InterestManagement.RefreshIntervalSnapshots; refresh++)
        {
            fixture.Presence.RefreshInterests();
        }

        var served = new List<ulong>();
        fixture.Presence.EncodeCombatantState = entityId =>
        {
            served.Add(entityId);
            return null;
        };
        var rate = fixture.Config.SnapshotRateHz;
        var refresher = new FarStateRefresher(fixture.Presence, fixture.Registry, fixture.Sender, fixture.FailureIsolation, rate);
        var cadence = new SimulationTickCadence(fixture.Config.MovementSimulation.TickRateHz, rate);
        var phases = new SimulationTickPhases();
        refresher.AddTickPhase(phases, cadence);
        var phase = Assert.Single(phases.InTickOrder());
        var ticksPerSecond = (uint)fixture.Config.MovementSimulation.TickRateHz;
        var grid = new PlayerSpatialGrid();
        var noPlayers = new List<Mmorpg.SimulationWorker.Entities.PlayerSimulationEntity>();

        // Every alignment of the wrap in the two seconds that a state may take: the run starts
        // between 1 and 60 ticks before the wrap, and goes on for 60 ticks.
        for (var offset = 1u; offset <= 2 * ticksPerSecond; offset++)
        {
            foreach (var other in others)
            {
                fixture.Presence.MarkMissed(viewer.Context.PeerId, other.Entity.NetworkEntityId, MissedStates.Combatant);
            }

            served.Clear();
            var start = unchecked(0u - offset);
            for (var step = 0u; step < 2 * ticksPerSecond; step++)
            {
                var tick = new SimulationTick(unchecked(start + step), new SimulationTickPlayers(noPlayers), grid);
                phase.Run(in tick);
            }

            Assert.Equal(
                others.Select(other => other.Entity.NetworkEntityId).Order(),
                served.Distinct().Order());
        }
    }

    private static SnapshotReplicator CreateReplicator(
        RealtimeLoopFixture fixture,
        IMobSnapshotSource mobs,
        uint lastSequence = 0)
    {
        return new SnapshotReplicator(
            fixture.Config,
            mobs,
            fixture.Entities,
            fixture.Bindings,
            fixture.Registry,
            fixture.Sender,
            fixture.Presence,
            fixture.Metrics,
            fixture.PerformanceMetrics,
            fixture.Clock,
            fixture.FailureIsolation,
            lastSnapshotSequence: lastSequence);
    }

    // Every connection sees every other once each had its refresh.
    private static void ShowEachOther(RealtimeLoopFixture fixture)
    {
        var interest = new InterestRefresher(fixture.Entities, fixture.Presence, fixture.PerformanceMetrics);
        for (var refresh = 0; refresh < fixture.Config.InterestManagement.RefreshIntervalSnapshots; refresh++)
        {
            interest.Refresh();
        }
    }

    private static uint[] OwnerSequences(ConnectedPlayer player)
    {
        return
        [
            .. player.Peer.Sent
                .Where(sent => sent.Type == RealtimeMessageType.OwnerSimulationSnapshot)
                .Select(sent =>
                {
                    Assert.True(RealtimeProtocol.TryDecodeOwnerSimulationSnapshot(sent.Data, out var snapshot, out var error), error);
                    return snapshot.SnapshotSequence;
                })
        ];
    }

    // The longest run of snapshots between two records, or from the first record to the end
    // of the window; sequences count by their distance from the first, which wraps.
    private static uint MaximumGap(List<uint> sequences)
    {
        uint maximum = 0;
        for (var index = 1; index < sequences.Count; index++)
        {
            maximum = Math.Max(maximum, unchecked(sequences[index] - sequences[index - 1]));
        }

        return maximum;
    }
}
