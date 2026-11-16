using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// What each recipient gets in a snapshot: new in view, near changes every snapshot, far changes on
// their far slot, stop repeats, refreshes, the budget order, deferral, and forgetting.
public sealed class SnapshotSchedulerTests
{
    private const ulong Own = 1;

    [Fact]
    public void AnEntityNewInViewComesAtOnceAndAStandingOneThenOnlyOnItsRefreshSlot()
    {
        var fixture = new Fixture();
        fixture.Stand(2, 3);

        // Every visible entity but the recipient's own is new.
        Assert.Equal([2u, 3u], fixture.Snapshot(near: [2, 3]));

        var sent = new Dictionary<ulong, List<uint>> { [2] = [], [3] = [] };
        for (var snapshot = 0; snapshot < 45; snapshot++)
        {
            foreach (var entityId in fixture.Snapshot(near: [2, 3]))
            {
                sent[entityId].Add(fixture.Sequence);
            }
        }

        // Once per 15 snapshots, on the slot where the sequence plus the id is a multiple of 15.
        Assert.All(sent[2], sequence => Assert.Equal(0ul, (sequence + 2ul) % 15ul));
        Assert.All(sent[3], sequence => Assert.Equal(0ul, (sequence + 3ul) % 15ul));
        Assert.Equal(3, sent[2].Count);
        Assert.Equal(3, sent[3].Count);
        Assert.Empty(sent[2].Intersect(sent[3]));
    }

    [Fact]
    public void ANearEntityThatWalksComesInEverySnapshotAndAFarOneOnItsFarSlot()
    {
        var fixture = new Fixture();
        fixture.Walk(4, 5);
        fixture.Snapshot(near: [4]);

        var near = new List<uint>();
        var far = new List<uint>();
        for (var snapshot = 0; snapshot < 12; snapshot++)
        {
            fixture.Walk(4, 5);
            var sent = fixture.Snapshot(near: [4]);
            if (sent.Contains(4u))
            {
                near.Add(fixture.Sequence);
            }

            if (sent.Contains(5u))
            {
                far.Add(fixture.Sequence);
            }
        }

        Assert.Equal(12, near.Count);
        Assert.Equal(4, far.Count);
        Assert.All(far, sequence => Assert.Equal(0ul, (sequence + 5ul) % 3ul));
    }

    // A client starts an entity that walks off from a stand one snapshot interval before its first
    // moving record, and the second gives it the walking speed, so a far one comes in both
    // snapshots of its start, off its far slot, and then on its slot again.
    [Fact]
    public void AFarEntityThatStartsToWalkFromAStandComesInTheNextTwoSnapshots()
    {
        var fixture = new Fixture();
        fixture.Stand(5);
        for (var snapshot = 0; snapshot < 20 || !fixture.IsFarSlot(5, fixture.Sequence + 3); snapshot++)
        {
            fixture.Snapshot(near: []);
        }

        for (var snapshot = 0; snapshot < 2; snapshot++)
        {
            fixture.Walk(5);
            Assert.Equal([5u], fixture.Snapshot(near: []));
            Assert.False(fixture.IsFarSlot(5, fixture.Sequence));
        }

        var sent = new List<uint>();
        for (var snapshot = 0; snapshot < 9; snapshot++)
        {
            fixture.Walk(5);
            if (fixture.Snapshot(near: []).Contains(5u))
            {
                sent.Add(fixture.Sequence);
            }
        }

        Assert.Equal(3, sent.Count);
        Assert.All(sent, sequence => Assert.True(fixture.IsFarSlot(5, sequence)));

        // A stand of one snapshot is a stand too: the walk after it comes at once, twice.
        while (!fixture.IsFarSlot(5, fixture.Sequence + 1))
        {
            fixture.Walk(5);
            fixture.Snapshot(near: []);
        }

        fixture.Snapshot(near: []);
        for (var snapshot = 0; snapshot < 2; snapshot++)
        {
            fixture.Walk(5);
            Assert.Contains(5u, fixture.Snapshot(near: []));
            Assert.False(fixture.IsFarSlot(5, fixture.Sequence));
        }
    }

    [Fact]
    public void ARecordThatStopsChangingIsRepeatedInTheNextThreeSnapshotsOfItsTier()
    {
        var fixture = new Fixture();
        fixture.Walk(4, 5);
        fixture.Snapshot(near: [4]);
        for (var snapshot = 0; snapshot < 6; snapshot++)
        {
            fixture.Walk(4, 5);
            fixture.Snapshot(near: [4]);
        }

        // Both stop; the far one's last change goes out on its next far slot first.
        var near = new List<uint>();
        var far = new List<uint>();
        for (var snapshot = 0; snapshot < 14; snapshot++)
        {
            var sent = fixture.Snapshot(near: [4]);
            if (sent.Contains(4u))
            {
                near.Add(fixture.Sequence);
            }

            if (sent.Contains(5u))
            {
                far.Add(fixture.Sequence);
            }
        }

        var stopped = near[0];
        Assert.Equal([stopped, stopped + 1, stopped + 2], near.Where(sequence => (sequence + 4) % 15 != 0));
        Assert.Equal(3, far.Count);
        Assert.All(far, sequence => Assert.Equal(0ul, (sequence + 5ul) % 3ul));
    }

    [Fact]
    public void TheBudgetTakesNewInViewThenTheNearChangesNearestFirstAndTheFartherInTurn()
    {
        // The walkers stand at x 10 to 89, as far from the recipient at 0 as their id.
        var fixture = new Fixture();
        var nearWalkers = Enumerable.Range(10, 80).Select(value => (ulong)value).ToArray();
        fixture.Walk(nearWalkers);
        fixture.Stand(200);
        fixture.Snapshot(near: nearWalkers);
        for (var snapshot = 0; snapshot < 15; snapshot++)
        {
            fixture.Walk(nearWalkers);
            fixture.Snapshot(near: nearWalkers);
        }

        // A newcomer and 80 near walkers in a budget of 40 records: the newcomer first, then the
        // 39 nearest walkers, and no refresh.
        fixture.Stand(300);
        fixture.Walk(nearWalkers);
        var budget = BudgetFor(40);
        var first = fixture.Snapshot(near: nearWalkers, budget: budget, out var plan);
        Assert.Equal(40, plan.RecordCount);
        Assert.Equal([.. Enumerable.Range(10, 39).Select(value => (uint)value), 300u], first);
        Assert.Equal(41 + (fixture.IsRefreshSlot(200) ? 1 : 0), plan.DeferredCount);

        // Then the nearest come in every snapshot and the farther ones in turn, less often the
        // farther they are, and every one comes.
        var counts = nearWalkers.ToDictionary(entityId => (uint)entityId, _ => 0);
        for (var snapshot = 0; snapshot < 30; snapshot++)
        {
            fixture.Walk(nearWalkers);
            foreach (var entityId in fixture.Snapshot(near: nearWalkers, budget: budget, out _).Where(counts.ContainsKey))
            {
                counts[entityId]++;
            }
        }

        Assert.Equal(30, counts[10]);
        Assert.Equal(30, counts[15]);
        Assert.All(counts.Values, count => Assert.InRange(count, 1, 30));
        Assert.True(counts[30] > counts[60], "A walker at 30 m comes more often than one at 60 m.");
        Assert.True(counts[60] > counts[89], "A walker at 60 m comes more often than one at 89 m.");
    }

    [Fact]
    public void ADueRecordThatWaitedLongerThanTheRefreshIntervalGoesBeforeTheNearChanges()
    {
        var fixture = new Fixture();
        var nearWalkers = Enumerable.Range(10, 80).Select(value => (ulong)value).ToArray();
        fixture.Walk(nearWalkers);
        fixture.Walk(500);
        fixture.Snapshot(near: nearWalkers);

        // The far walker gets nothing while 80 near walkers fill a budget of 40 records, until it
        // waited longer than a second; then it comes first.
        var budget = BudgetFor(40);
        var sentAt = new List<uint>();
        for (var snapshot = 0; snapshot < 40; snapshot++)
        {
            fixture.Walk(nearWalkers);
            fixture.Walk(500);
            if (fixture.Snapshot(near: nearWalkers, budget: budget, out _).Contains(500u))
            {
                sentAt.Add(fixture.Sequence);
            }
        }

        Assert.Equal([17u, 33u], sentAt);
    }

    [Fact]
    public void ADeferredFarChangeOrRefreshStaysDueInTheNextSnapshot()
    {
        var fixture = new Fixture();
        fixture.Walk(5);
        fixture.Snapshot(near: []);
        fixture.Walk(5);
        while (!fixture.IsFarSlot(5, fixture.Sequence + 1))
        {
            fixture.Snapshot(near: []);
        }

        // On its far slot nothing fits; in the snapshot after, off its slot, it comes.
        Assert.Empty(fixture.Snapshot(near: [], budget: 0, out var deferredPlan));
        Assert.Equal(1, deferredPlan.DeferredCount);
        Assert.Equal([5u], fixture.Snapshot(near: []));
    }

    [Fact]
    public void RecordsThatDidNotGoOutStayDue()
    {
        var fixture = new Fixture();
        fixture.Stand(2, 3);

        Assert.Equal(2, fixture.Plan(near: [], out _).RecordCount);
        fixture.Complete(sent: false);

        Assert.Equal([2u, 3u], fixture.Snapshot(near: []));
    }

    [Fact]
    public void AnEntityThatLeftTheViewIsForgottenAndIsNewAgainWhenItReturns()
    {
        var fixture = new Fixture();
        fixture.Stand(2, 3);
        fixture.Snapshot(near: []);
        fixture.Visible = [Own, 2];
        fixture.Snapshot(near: []);
        Assert.Equal([Own, 2ul], fixture.State.Entities.Select(entry => entry.EntityId));

        fixture.Visible = [Own, 2, 3];
        Assert.Equal([3u], fixture.Snapshot(near: []).Where(entityId => !fixture.IsRefreshSlot(entityId)));
    }

    [Fact]
    public void AVisibleEntityWithoutARecordIsSkipped()
    {
        var fixture = new Fixture();
        fixture.Stand(2);
        fixture.Visible = [Own, 2, 99];

        Assert.Equal([2u], fixture.Snapshot(near: [2, 99]));
    }

    private static int BudgetFor(int records)
    {
        // The owner snapshot's share and the 95 percent target come off the budget.
        var remote = SnapshotRecords.CalculateEncodedBytes(records) + RealtimeProtocol.OwnerSimulationSnapshotPacketSize;
        return (int)Math.Ceiling(remote / 0.95) + 1;
    }

    private sealed class Fixture
    {
        private readonly SnapshotScheduler scheduler = new(SnapshotReplicationConfig.Default);
        private readonly Dictionary<ulong, float> positions = [];

        public SnapshotRecords Records { get; } = new();

        public SnapshotRecipientState State { get; } = new(new RealtimePeerContext(1, DateTime.UtcNow, UdpQuotaConfig.Default), Own);

        public uint Sequence { get; private set; }

        // The recipient's own entity is visible too, and never sent to it.
        public ulong[]? Visible { get; set; }

        public void Stand(params ulong[] entityIds)
        {
            foreach (var entityId in entityIds)
            {
                positions.TryAdd(entityId, entityId);
            }
        }

        public void Walk(params ulong[] entityIds)
        {
            foreach (var entityId in entityIds)
            {
                positions[entityId] = positions.TryGetValue(entityId, out var x) ? x + 0.15f : entityId;
            }
        }

        public bool IsFarSlot(ulong entityId, uint sequence)
        {
            return (sequence + entityId) % 3 == 0;
        }

        public bool IsRefreshSlot(ulong entityId)
        {
            return (Sequence + entityId) % 15 == 0;
        }

        public SnapshotRecipientPlan Plan(ulong[] near, out ulong[] visible, int budget = int.MaxValue)
        {
            Sequence++;
            Records.BeginSnapshot(Sequence);
            Records.Report(Own, new PlayerMovementState(0f, 0f, 0f, 0f, 0f, 0f, 0f, true));
            foreach (var (entityId, x) in positions.OrderBy(pair => pair.Key))
            {
                Records.Report(entityId, new PlayerMovementState(x, 0f, 0f, 0f, 0f, 0f, 0f, true));
            }

            Records.EndReports();
            visible = Visible ?? [Own, .. positions.Keys.Order()];
            var nearFlags = visible.Select(near.Contains).ToArray();
            return scheduler.Plan(Records, State, visible, nearFlags, budget);
        }

        public void Complete(bool sent)
        {
            scheduler.Complete(sent);
        }

        public uint[] Snapshot(ulong[] near)
        {
            return Snapshot(near, int.MaxValue, out _);
        }

        public uint[] Snapshot(ulong[] near, int budget, out SnapshotRecipientPlan plan)
        {
            plan = Plan(near, out _, budget);
            var sent = new List<uint>();
            for (var chunk = 0; chunk < plan.PacketCount; chunk++)
            {
                var packet = scheduler.WriteChunk(chunk, Sequence, out var recordCount).ToArray();
                Assert.True(RealtimeProtocol.TryDecodeSimulationSnapshot(packet, out var decoded, out var error), error);
                Assert.Equal(recordCount, decoded.Entities.Length);
                Assert.Equal(Sequence, decoded.SnapshotSequence);
                sent.AddRange(decoded.Entities.Select(entity => entity.EntityId));
            }

            scheduler.Complete(sent: true);
            return [.. sent.Order()];
        }
    }
}
