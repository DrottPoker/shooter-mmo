using System.Diagnostics;
using Mmorpg.GameProtocol;
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
                .Select(entityId => new RealtimeEntitySnapshot(
                    entityId,
                    1,
                    new RealtimePlayerState(0f, 0f, 0f, 0f, 0f, 0f, 0f, true)))
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
        const int entityCount = 400;
        var visible = Enumerable.Range(1, entityCount)
            .Select(index => (ulong)index)
            .ToArray();
        var cache = CreateSnapshotCache(visible);
        var bytesPerRecipient = (38 * 1024 * 1024) / 15 / entityCount;

        var first = cache.GetOrCreate(visible, 1, 1, bytesPerRecipient, 9_500);
        var firstIds = DecodeEntityIds(first);
        cache.Reset();
        foreach (var entityId in visible)
        {
            AddSnapshot(cache, entityId);
        }

        var second = cache.GetOrCreate(visible, 2, 2, bytesPerRecipient, 9_500);
        var secondIds = DecodeEntityIds(second);

        Assert.Equal(149, first.EntityCount);
        Assert.Equal(251, first.DeferredEntityCount);
        Assert.Equal(7, first.Packets.Count);
        Assert.True(
            first.TotalBytes + RealtimeProtocol.OwnerSimulationSnapshotPacketSize
                <= (bytesPerRecipient * 9_500) / 10_000);
        Assert.Empty(firstIds.Intersect(secondIds));
        Assert.Equal(298, firstIds.Concat(secondIds).Distinct().Count());
    }

    [Fact]
    public void SnapshotBudgetKeepsConfiguredHeadroomBeforeAggregateBackpressure()
    {
        const int entityCount = 250;
        var visible = Enumerable.Range(1, entityCount)
            .Select(index => (ulong)index)
            .ToArray();
        var cache = CreateSnapshotCache(visible);
        var bytesPerRecipient = (38 * 1024 * 1024) / 15 / entityCount;

        var batch = cache.GetOrCreate(visible, 1, 1, bytesPerRecipient, 9_500);

        // The 54-byte owner snapshot shares the budget, so 239 remote entities fit.
        Assert.Equal(239, batch.EntityCount);
        Assert.Equal(11, batch.DeferredEntityCount);
        Assert.Equal(10, batch.Packets.Count);
        Assert.True(
            batch.TotalBytes + RealtimeProtocol.OwnerSimulationSnapshotPacketSize
                <= (bytesPerRecipient * 9_500) / 10_000);
    }

    private static SnapshotPacketCache CreateSnapshotCache(IEnumerable<ulong> entityIds)
    {
        var cache = new SnapshotPacketCache();
        foreach (var entityId in entityIds)
        {
            AddSnapshot(cache, entityId);
        }

        return cache;
    }

    private static void AddSnapshot(SnapshotPacketCache cache, ulong entityId)
    {
        cache.Add(new RealtimeEntitySnapshot(
            entityId,
            1,
            new RealtimePlayerState(0f, 0f, 0f, 0f, 0f, 0f, 0f, true)));
    }

    private static IReadOnlyList<ulong> DecodeEntityIds(SnapshotPacketBatch batch)
    {
        var entityIds = new List<ulong>();
        foreach (var packet in batch.Packets)
        {
            Assert.True(
                RealtimeProtocol.TryDecodeSimulationSnapshot(
                    packet,
                    out var snapshot,
                    out var error),
                error);
            entityIds.AddRange(snapshot.Entities.Select(entity => entity.EntityId));
        }

        return entityIds;
    }
}
