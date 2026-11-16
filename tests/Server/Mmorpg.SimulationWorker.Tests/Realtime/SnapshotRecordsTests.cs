using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class SnapshotRecordsTests
{
    [Fact]
    public void ARecordKeepsItsVersionUntilItChangesAndAnUnreportedEntityLosesItsSlot()
    {
        var records = new SnapshotRecords();
        Snapshot(records, 1, (1, State(1f)), (2, State(2f)), (3, State(3f)));
        Assert.True(records.TryGetSlot(2, out var slot));
        var first = records.VersionOf(slot);

        // Less than half a step moves nothing on the wire.
        Snapshot(records, 2, (1, State(1f)), (2, State(2.001f)), (3, State(3f)));
        Assert.True(records.TryGetSlot(2, out slot));
        Assert.Equal(first, records.VersionOf(slot));

        Snapshot(records, 3, (1, State(1f)), (2, State(2.5f)), (3, State(3f)));
        Assert.True(records.TryGetSlot(2, out slot));
        Assert.True(records.VersionOf(slot) > first);

        // Entity 1 is gone; the others keep their records in their new slots.
        Snapshot(records, 4, (2, State(2.5f)), (3, State(3f)));
        Assert.Equal(2, records.Count);
        Assert.False(records.TryGetSlot(1, out _));
        Assert.True(records.TryGetSlot(3, out var third));
        Assert.Equal(3ul, records.EntityIdOf(third));
        Assert.Equal(Quantized(3, State(3f)), records.Record(third).ToArray());
    }

    // A start is a position that moves after it stood for a snapshot, as a client judges standing
    // by the position, and it lasts that snapshot and the next: a new entity, a walk that goes on,
    // and a turn in place start nothing.
    [Fact]
    public void APositionThatMovesAfterItStoodStartsForTwoSnapshots()
    {
        var records = new SnapshotRecords();
        Snapshot(records, 1, (1, State(1f)), (2, State(2f)));
        Assert.Equal((false, false), (StartOf(records, 1), StartOf(records, 2)));

        // Entity 1 walks off at once, and entity 2 turns where it stands.
        Snapshot(records, 2, (1, State(1.15f)), (2, State(2f, yawDegrees: 90f)));
        Assert.Equal((false, false), (StartOf(records, 1), StartOf(records, 2)));

        // Entity 1 walks on, and entity 2 walks off after it stood.
        Snapshot(records, 3, (1, State(1.3f)), (2, State(2.15f, yawDegrees: 90f)));
        Assert.Equal((false, true), (StartOf(records, 1), StartOf(records, 2)));

        // Entity 1 stands for one snapshot; entity 2 walks on in the second snapshot of its start.
        Snapshot(records, 4, (1, State(1.3f)), (2, State(2.3f, yawDegrees: 90f)));
        Assert.Equal((false, true), (StartOf(records, 1), StartOf(records, 2)));

        // Entity 1 walks off again; entity 2 walks on, its start over.
        Snapshot(records, 5, (1, State(1.45f)), (2, State(2.45f, yawDegrees: 90f)));
        Assert.Equal((true, false), (StartOf(records, 1), StartOf(records, 2)));

        // A start lasts its second snapshot also when the entity stopped in it.
        Snapshot(records, 6, (1, State(1.45f)), (2, State(2.6f, yawDegrees: 90f)));
        Assert.Equal((true, false), (StartOf(records, 1), StartOf(records, 2)));
        Snapshot(records, 7, (1, State(1.45f)), (2, State(2.75f, yawDegrees: 90f)));
        Assert.Equal((false, false), (StartOf(records, 1), StartOf(records, 2)));

        // When entity 1's slot goes, entity 2 takes it with its own past: it stands and walks off.
        Snapshot(records, 8, (2, State(2.75f, yawDegrees: 90f)));
        Snapshot(records, 9, (2, State(2.9f, yawDegrees: 90f)));
        Assert.True(StartOf(records, 2));
        Snapshot(records, 10, (2, State(3.05f, yawDegrees: 90f)));
        Assert.True(StartOf(records, 2));
        Snapshot(records, 11, (2, State(3.2f, yawDegrees: 90f)));
        Assert.False(StartOf(records, 2));
    }

    [Fact]
    public void AStateThatIsNotFiniteGetsNoRecordAndAnEntityIsReportedOnce()
    {
        var records = new SnapshotRecords();
        records.BeginSnapshot(1);

        Assert.False(records.Report(1, State(float.NaN)));
        Assert.False(records.Report(2, State(0f, yawDegrees: float.PositiveInfinity)));
        Assert.True(records.Report(3, State(0f)));
        Assert.Throws<InvalidOperationException>(() => records.Report(3, State(1f)));
        records.EndReports();
        Assert.Equal(1, records.Count);
    }

    [Fact]
    public void PositionsBeyondTheRangeAreClamped()
    {
        var records = new SnapshotRecords();
        Snapshot(records, 1, (1, State(100_000f, 600f, -100_000f, -90f)));
        Assert.True(records.TryGetSlot(1, out var slot));

        var entity = Decode(records.Record(slot));

        Assert.Equal((1 << 23) - 1, entity.QuantizedX);
        Assert.Equal(short.MaxValue, entity.QuantizedY);
        Assert.Equal(-(1 << 23), entity.QuantizedZ);
        Assert.Equal(768, entity.YawStep);
    }

    [Fact]
    public void TheMostRecordsThatFitABudgetAreThoseWhosePacketsFit()
    {
        for (var budget = -1; budget <= 3 * 1023; budget++)
        {
            foreach (var available in new[] { 0, 1, 70, 71, 72, 142, 143, 300 })
            {
                var count = 0;
                while (count < available && SnapshotRecords.CalculateEncodedBytes(count + 1) <= budget)
                {
                    count++;
                }

                Assert.Equal(count, SnapshotRecords.CalculateMaximumEntityCount(budget, available));
            }
        }
    }

    [Fact]
    public void ReportingPlanningAndBuildingChunksAllocateNothing()
    {
        var records = new SnapshotRecords();
        var scheduler = new SnapshotScheduler(SnapshotReplicationConfig.Default);
        var state = new SnapshotRecipientState(new RealtimePeerContext(1, DateTime.UtcNow, UdpQuotaConfig.Default), 1);
        var visible = Enumerable.Range(1, 200).Select(value => (ulong)value).ToArray();
        var near = visible.Select(entityId => entityId % 2 == 0).ToArray();
        void Broadcast(uint sequence)
        {
            records.BeginSnapshot(sequence);
            foreach (var entityId in visible)
            {
                // Half of them walk.
                records.Report(entityId, State(entityId + (entityId % 2 == 0 ? sequence * 0.15f : 0f), 1f, 2f, 3f));
            }

            records.EndReports();

            // About 60 records fit, fewer than the near walkers, so the budget order runs too.
            var plan = scheduler.Plan(records, state, visible, near, 1_000);
            for (var chunk = 0; chunk < plan.PacketCount; chunk++)
            {
                scheduler.WriteChunk(chunk, sequence, out _);
            }

            scheduler.Complete(sent: true);
        }

        // The first broadcasts grow the buffers to the population.
        for (uint sequence = 1; sequence < 20; sequence++)
        {
            Broadcast(sequence);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (uint sequence = 20; sequence < 60; sequence++)
        {
            Broadcast(sequence);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    internal static PlayerMovementState State(float positionX, float positionY = 0f, float positionZ = 0f, float yawDegrees = 0f)
    {
        return new PlayerMovementState(positionX, positionY, positionZ, 1f, 0f, 1f, yawDegrees, true);
    }

    internal static void Snapshot(SnapshotRecords records, uint sequence, params (ulong EntityId, PlayerMovementState State)[] states)
    {
        records.BeginSnapshot(sequence);
        foreach (var (entityId, state) in states)
        {
            Assert.True(records.Report(entityId, state));
        }

        records.EndReports();
    }

    private static bool StartOf(SnapshotRecords records, ulong entityId)
    {
        Assert.True(records.TryGetSlot(entityId, out var slot));
        return records.Starts[slot];
    }

    private static byte[] Quantized(uint entityId, PlayerMovementState state)
    {
        var record = new byte[RealtimeProtocol.SimulationSnapshotEntitySize];
        var quantized = RealtimeEntitySnapshot.Quantize(entityId, state.PositionX, state.PositionY, state.PositionZ, state.YawDegrees, state.IsGrounded);
        RealtimeProtocol.EncodeEntitySnapshot(
            record,
            quantized.EntityId,
            quantized.QuantizedX,
            quantized.QuantizedY,
            quantized.QuantizedZ,
            quantized.YawStep,
            quantized.IsGrounded);
        return record;
    }

    private static RealtimeEntitySnapshot Decode(ReadOnlySpan<byte> record)
    {
        var packet = new byte[RealtimeProtocol.SimulationSnapshotPacketHeaderSize + record.Length];
        var length = RealtimeProtocol.EncodeSimulationSnapshotHeader(packet, 1, 1, 0, 1, 1);
        record.CopyTo(packet.AsSpan(length));
        Assert.True(RealtimeProtocol.TryDecodeSimulationSnapshot(packet, out var snapshot, out var error), error);
        return Assert.Single(snapshot.Entities);
    }
}
