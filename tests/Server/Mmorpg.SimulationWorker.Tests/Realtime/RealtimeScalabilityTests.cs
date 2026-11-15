using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class RealtimeScalabilityTests
{
    [Fact]
    [Trait("Category", "Load")]
    public void IncrementalDenseJoinsBuildVisibilityWithoutGlobalRefreshes()
    {
        const int entityCount = 500;
        var manager = new SimulationInterestManager(
            new InterestManagementConfig(64f, 128f, 144f));
        var stopwatch = Stopwatch.StartNew();

        for (var index = 1; index <= entityCount; index++)
        {
            var enteredConnections = manager.AddEntity(
                new SimulationInterestEntity((ulong)index, 0f, 0f));
            Assert.Equal(index - 1, enteredConnections.Count);

            var joiningInterest = manager.Refresh(index, (ulong)index);
            Assert.Equal(index, joiningInterest.Visible.Count);
        }

        stopwatch.Stop();

        Assert.Equal(entityCount, manager.GetVisibleOrdered(1).Count);
        Assert.Equal(entityCount, manager.GetVisibleOrdered(entityCount).Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Incremental dense join workload exceeded its five-second budget: {stopwatch.Elapsed}.");
    }

    [Fact]
    public void MovesInsideOneCellReachTheVisibilityTest()
    {
        var manager = new SimulationInterestManager(
            new InterestManagementConfig(64f, 20f, 25f));
        manager.AddEntity(new SimulationInterestEntity(1, 0f, 0f));
        manager.AddEntity(new SimulationInterestEntity(2, 10f, 0f));
        Assert.Contains(2ul, manager.Refresh(1, 1).Visible);

        // Still in the same 64 m cell, but beyond the exit radius.
        manager.UpdateEntity(new SimulationInterestEntity(2, 40f, 0f));
        var update = manager.Refresh(1, 1);

        Assert.Equal([2ul], update.Exited);
        Assert.DoesNotContain(2ul, update.Visible);
    }

    [Fact]
    public void EntitiesBetweenTheRadiiStayVisibleOnlyWhenAlreadySeen()
    {
        var manager = new SimulationInterestManager(
            new InterestManagementConfig(64f, 20f, 25f));
        manager.Rebuild([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 15f, 0f),
            new SimulationInterestEntity(3, 22f, 0f)
        ]);
        var first = manager.Refresh(1, 1);
        Assert.Equal([1ul, 2ul], manager.GetVisibleOrdered(1));
        Assert.DoesNotContain(3ul, first.Visible);

        manager.Rebuild([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 22f, 0f),
            new SimulationInterestEntity(3, 22f, 1f)
        ]);
        manager.Refresh(1, 1);

        Assert.Equal([1ul, 2ul], manager.GetVisibleOrdered(1));
    }

    [Fact]
    public void TheNearTierIsDecidedAtTheRefreshWithHysteresis()
    {
        var manager = new SimulationInterestManager(new InterestManagementConfig(64f, 128f, 144f, 3, 48f, 56f));
        manager.Rebuild([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 40f, 0f),
            new SimulationInterestEntity(3, 52f, 0f),
            new SimulationInterestEntity(4, 100f, 0f)
        ]);
        manager.Refresh(1, 1);
        Assert.Equal([1ul, 2ul, 3ul, 4ul], manager.GetVisibleOrdered(1));
        Assert.Equal([true, true, false, false], manager.GetNearOrdered(1));

        // Between 48 and 56 m a near entity stays near and a far one stays far; beyond 56 m it
        // leaves. Nothing changes before the connection's next refresh.
        manager.Rebuild([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 50f, 0f),
            new SimulationInterestEntity(3, 50f, 1f),
            new SimulationInterestEntity(4, 30f, 0f)
        ]);
        Assert.Equal([true, true, false, false], manager.GetNearOrdered(1));
        manager.Refresh(1, 1);
        Assert.Equal([true, true, false, true], manager.GetNearOrdered(1));
        manager.Rebuild([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 57f, 0f),
            new SimulationInterestEntity(3, 50f, 1f),
            new SimulationInterestEntity(4, 30f, 0f)
        ]);
        manager.Refresh(1, 1);
        Assert.Equal([true, false, false, true], manager.GetNearOrdered(1));
        Assert.False(manager.IsNear(1, 2));
        Assert.True(manager.IsNear(1, 4));

        // A joining entity takes its tier at once, and a forgotten one leaves both lists.
        manager.AddEntity(new SimulationInterestEntity(5, 10f, 0f));
        manager.AddEntity(new SimulationInterestEntity(6, 70f, 0f));
        Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul, 6ul], manager.GetVisibleOrdered(1));
        Assert.Equal([true, false, false, true, true, false], manager.GetNearOrdered(1));
        manager.ForgetEntity(4);
        Assert.Equal([1ul, 2ul, 3ul, 5ul, 6ul], manager.GetVisibleOrdered(1));
        Assert.Equal([true, false, false, true, false], manager.GetNearOrdered(1));
    }

    [Fact]
    [Trait("Category", "Load")]
    public void SpatialInterestAndSnapshotEncodingRemainBoundedAtLargeEntityCounts()
    {
        const int entityCount = 25_000;
        const int observerCount = 1_000;
        var manager = new SimulationInterestManager(
            new InterestManagementConfig(64f, 96f, 112f));
        var entities = Enumerable.Range(1, entityCount)
            .Select(index => new SimulationInterestEntity(
                (ulong)index,
                (index % 250) * 16f,
                (index / 250) * 16f))
            .ToArray();
        var stopwatch = Stopwatch.StartNew();
        manager.Rebuild(entities);

        long totalVisible = 0;
        for (var connectionId = 1; connectionId <= observerCount; connectionId++)
        {
            var controlledEntityId = (ulong)(((connectionId - 1) * 23) + 1);
            var update = manager.Refresh(connectionId, controlledEntityId);
            totalVisible += update.Visible.Count;
            var snapshotEntities = update.Visible
                .Take(RealtimeProtocol.MaximumSnapshotEntitiesPerChunk)
                .Select(entityId => new RealtimeEntitySnapshot((uint)entityId, 0, 0, 0, 0, true))
                .ToArray();
            var packet = RealtimeProtocol.EncodeSimulationSnapshot(
                new RealtimeSimulationSnapshot(1, 1, 0, 1, snapshotEntities));
            Assert.True(RealtimeProtocol.TryDecodeSimulationSnapshot(packet, out _, out var error), error);
        }

        stopwatch.Stop();

        Assert.InRange(totalVisible, observerCount, 250_000);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Realtime scalability workload exceeded its five-second budget: {stopwatch.Elapsed}.");
    }

    [Fact]
    public void DenseSnapshotBudgetRotatesRemoteEntitiesWithoutExceedingFairShare()
    {
        const int entityCount = 1_000;
        var visible = Enumerable.Range(1, entityCount)
            .Select(index => (ulong)index)
            .ToArray();
        var bytesPerRecipient = (38 * 1024 * 1024) / 15 / entityCount;
        var budget = new BudgetFixture(visible);

        var first = budget.Plan(1, bytesPerRecipient, out var firstIds);
        var second = budget.Plan(2, bytesPerRecipient, out var secondIds);

        Assert.Equal(172, first.RecordCount);
        Assert.Equal(828, first.DeferredCount);
        Assert.Equal(3, first.PacketCount);
        Assert.True(
            first.TotalBytes + RealtimeProtocol.OwnerSimulationSnapshotPacketSize
                <= (bytesPerRecipient * 9_500) / 10_000);
        Assert.Equal(172, second.RecordCount);
        Assert.Empty(firstIds.Intersect(secondIds));
        Assert.Equal(344, firstIds.Concat(secondIds).Distinct().Count());
    }

    [Fact]
    public void SnapshotBudgetKeepsConfiguredHeadroomBeforeAggregateBackpressure()
    {
        const int entityCount = 600;
        var visible = Enumerable.Range(1, entityCount)
            .Select(index => (ulong)index)
            .ToArray();
        var bytesPerRecipient = (38 * 1024 * 1024) / 15 / entityCount;

        var plan = new BudgetFixture(visible).Plan(1, bytesPerRecipient, out _);

        // The 54-byte owner snapshot shares the budget, so 289 remote entities fit.
        Assert.Equal(289, plan.RecordCount);
        Assert.Equal(311, plan.DeferredCount);
        Assert.Equal(5, plan.PacketCount);
        Assert.True(
            plan.TotalBytes + RealtimeProtocol.OwnerSimulationSnapshotPacketSize
                <= (bytesPerRecipient * 9_500) / 10_000);
    }

    // One recipient that sees every entity, all new in view, so the budget alone decides.
    private sealed class BudgetFixture(ulong[] visible)
    {
        private readonly SnapshotRecords records = new();
        private readonly SnapshotScheduler scheduler = new(SnapshotReplicationConfig.Default);
        private readonly SnapshotRecipientState state = new(new RealtimePeerContext(1, DateTime.UtcNow, UdpQuotaConfig.Default), ulong.MaxValue);
        private readonly bool[] near = new bool[visible.Length];

        public SnapshotRecipientPlan Plan(uint sequence, int bytesPerRecipient, out IReadOnlyList<ulong> entityIds)
        {
            records.BeginSnapshot(sequence);
            foreach (var entityId in visible)
            {
                records.Report(entityId, new PlayerMovementState(0f, 0f, 0f, 0f, 0f, 0f, 0f, true));
            }

            records.EndReports();
            var plan = scheduler.Plan(records, state, visible, near, bytesPerRecipient);
            var ids = new List<ulong>();
            for (var chunk = 0; chunk < plan.PacketCount; chunk++)
            {
                Assert.True(
                    RealtimeProtocol.TryDecodeSimulationSnapshot(
                        scheduler.WriteChunk(chunk, sequence, out _).ToArray(),
                        out var snapshot,
                        out var error),
                    error);
                ids.AddRange(snapshot.Entities.Select(entity => (ulong)entity.EntityId));
            }

            scheduler.Complete(sent: true);
            entityIds = ids;
            return plan;
        }
    }
}
