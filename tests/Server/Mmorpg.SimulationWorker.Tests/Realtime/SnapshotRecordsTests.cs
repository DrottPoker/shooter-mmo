using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class SnapshotRecordsTests
{
    [Fact]
    public void ARecipientsChunksCarryTheQuantizedRecordsItSees()
    {
        var records = new SnapshotRecords();
        for (ulong entityId = 1; entityId <= 80; entityId++)
        {
            Assert.True(records.Add(entityId, State(entityId * 1.001f, 0.25f, entityId * -2.5f, entityId * 4f)));
        }

        var visible = Enumerable.Range(1, 80).Select(value => (ulong)value).ToArray();
        var plan = records.Plan(visible, 7, int.MaxValue, 9_500);

        Assert.Equal(new SnapshotRecipientPlan(80, 0, 2, (80 * 14) + (2 * 20)), plan);
        var decoded = Chunks(records, plan, 7, 11);
        Assert.Equal([71, 9], decoded.Select(chunk => chunk.Entities.Length));
        Assert.All(decoded, chunk =>
        {
            Assert.Equal(7u, chunk.SnapshotSequence);
            Assert.Equal(11u, chunk.ServerTick);
            Assert.Equal(2, chunk.ChunkCount);
        });
        Assert.Equal([0, 1], decoded.Select(chunk => (int)chunk.ChunkIndex));
        var entities = decoded.SelectMany(chunk => chunk.Entities).ToArray();
        Assert.Equal(visible, entities.Select(entity => (ulong)entity.EntityId));
        foreach (var entity in entities)
        {
            // Within half a step of 1/128 m, 1/64 m, and 360/1024 degrees.
            Assert.InRange(Math.Abs(entity.PositionX - (entity.EntityId * 1.001f)), 0f, 0.5f / 128f);
            Assert.Equal(0.25f, entity.PositionY);
            Assert.InRange(Math.Abs(entity.PositionZ + (entity.EntityId * 2.5f)), 0f, 0.5f / 128f);
            Assert.InRange(Math.Abs(entity.YawDegrees - (entity.EntityId * 4f % 360f)), 0f, 360f / 1024 / 2);
            Assert.True(entity.IsGrounded);
        }
    }

    [Fact]
    public void ARecipientGetsOnlyTheVisibleEntitiesThatHaveARecord()
    {
        var records = new SnapshotRecords();
        records.Add(1, State(1f));
        records.Add(2, State(2f));

        var plan = records.Plan([1, 2, 3], 1, int.MaxValue, 9_500);

        Assert.Equal(new SnapshotRecipientPlan(2, 0, 1, (2 * 14) + 20), plan);
        Assert.Equal([1u, 2u], Chunks(records, plan, 1, 1).Single().Entities.Select(entity => entity.EntityId));
        Assert.Equal(default, records.Plan([3, 4], 1, int.MaxValue, 9_500));
    }

    [Fact]
    public void AStateThatIsNotFiniteGetsNoRecordAndAnEntityOnlyOne()
    {
        var records = new SnapshotRecords();

        Assert.False(records.Add(1, State(float.NaN)));
        Assert.False(records.Add(2, State(0f, yawDegrees: float.PositiveInfinity)));
        Assert.True(records.Add(3, State(0f)));
        Assert.Equal(1, records.Count);
        Assert.Throws<InvalidOperationException>(() => records.Add(3, State(1f)));
    }

    [Fact]
    public void PositionsBeyondTheRangeAreClamped()
    {
        var records = new SnapshotRecords();
        records.Add(1, State(100_000f, 600f, -100_000f, -90f));

        var entity = Chunks(records, records.Plan([1], 1, int.MaxValue, 9_500), 1, 1).Single().Entities.Single();

        Assert.Equal((1 << 23) - 1, entity.QuantizedX);
        Assert.Equal(short.MaxValue, entity.QuantizedY);
        Assert.Equal(-(1 << 23), entity.QuantizedZ);
        Assert.Equal(768, entity.YawStep);
    }

    [Fact]
    public void PlanningAndBuildingChunksAllocateNothing()
    {
        var records = new SnapshotRecords();
        var visible = Enumerable.Range(1, 200).Select(value => (ulong)value).ToArray();
        void Broadcast(uint sequence)
        {
            records.Reset();
            foreach (var entityId in visible)
            {
                records.Add(entityId, State(entityId, 1f, 2f, 3f));
            }

            var plan = records.Plan(visible, sequence, 2_000, 9_500);
            for (var chunk = 0; chunk < plan.PacketCount; chunk++)
            {
                records.WriteChunk(chunk, sequence, sequence, out _);
            }
        }

        // The first broadcast grows the buffers to the population.
        Broadcast(1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (uint sequence = 2; sequence < 20; sequence++)
        {
            Broadcast(sequence);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static PlayerMovementState State(float positionX, float positionY = 0f, float positionZ = 0f, float yawDegrees = 0f)
    {
        return new PlayerMovementState(positionX, positionY, positionZ, 1f, 0f, 1f, yawDegrees, true);
    }

    private static RealtimeSimulationSnapshot[] Chunks(SnapshotRecords records, SnapshotRecipientPlan plan, uint sequence, uint serverTick)
    {
        var chunks = new RealtimeSimulationSnapshot[plan.PacketCount];
        var totalBytes = 0;
        for (var index = 0; index < plan.PacketCount; index++)
        {
            var packet = records.WriteChunk(index, sequence, serverTick, out var recordCount).ToArray();
            totalBytes += packet.Length;
            Assert.True(RealtimeProtocol.TryDecodeSimulationSnapshot(packet, out var decoded, out var error), error);
            Assert.Equal(recordCount, decoded.Entities.Length);
            chunks[index] = decoded;
        }

        Assert.Equal(plan.TotalBytes, totalBytes);
        return chunks;
    }
}
