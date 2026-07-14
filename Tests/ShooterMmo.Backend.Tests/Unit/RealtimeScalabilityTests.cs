using System.Diagnostics;
using ShooterMmo.GameProtocol;
using SimulationWorker.Config;
using SimulationWorker.Realtime;

namespace ShooterMmo.Backend.Tests.Unit;

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
                    new RealtimePlayerState(0f, 0f, 0f, 0f, 0f, 0f, 0f, true, false)))
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
}
