using System.Diagnostics;
using ShooterMmo.GameSimulation;
using SimulationWorker.Config;
using SimulationWorker.Realtime;
using SimulationWorker.Sessions;
using SimulationWorker.WorldCollision;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class RealtimeResilienceTests
{
    [Fact]
    public async Task BoundedAsyncNeverExceedsConfiguredConcurrency()
    {
        const int maximumConcurrency = 4;
        var active = 0;
        var observedMaximum = 0;

        await BoundedAsync.ForEachAsync(
            Enumerable.Range(0, 40),
            maximumConcurrency,
            async (_, token) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref observedMaximum, current);
                await Task.Delay(5, token);
                Interlocked.Decrement(ref active);
            },
            CancellationToken.None);

        Assert.Equal(maximumConcurrency, observedMaximum);
        Assert.Equal(0, active);
    }

    [Fact]
    public void TokenBucketRejectsBurstAndRefillsAtTheSustainedRate()
    {
        var bucket = new TokenBucket(10d, 20d);
        var start = Stopwatch.GetTimestamp();

        Assert.True(bucket.TryConsume(20d, start));
        Assert.False(bucket.TryConsume(1d, start));
        Assert.True(bucket.TryConsume(10d, start + Stopwatch.Frequency));
        Assert.False(bucket.TryConsume(1d, start + Stopwatch.Frequency));
    }

    [Fact]
    public void TokenBucketSupportsABurstSmallerThanOneSecondOfRefill()
    {
        var bucket = new TokenBucket(100d, 10d);
        var start = Stopwatch.GetTimestamp();

        Assert.True(bucket.TryConsume(10d, start));
        Assert.False(bucket.TryConsume(1d, start));
        Assert.True(bucket.TryConsume(10d, start + (Stopwatch.Frequency / 10)));
    }

    [Fact]
    public void SnapshotRecipientRotationAdvancesPastTheAdmittedGroup()
    {
        var rotation = new SnapshotRecipientRotation();

        Assert.Equal(0, rotation.Begin(400));
        rotation.Complete(400, 159);
        Assert.Equal(160, rotation.Begin(400));
        rotation.Complete(400, 159);
        Assert.Equal(320, rotation.Begin(400));
        rotation.Complete(400, 159);
        Assert.Equal(80, rotation.Begin(400));
    }

    [Fact]
    public void SnapshotRecipientRotationStillAdvancesWhenNoRecipientIsAdmitted()
    {
        var rotation = new SnapshotRecipientRotation();

        Assert.Equal(0, rotation.Begin(10));
        rotation.Complete(10, -1);
        Assert.Equal(1, rotation.Begin(10));
    }

    [Fact]
    public void InterestManagerUsesEnterAndExitHysteresis()
    {
        var manager = new SimulationInterestManager(
            new InterestManagementConfig(10f, 20f, 25f));
        manager.Rebuild([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 19f, 0f),
            new SimulationInterestEntity(3, 30f, 0f)
        ]);

        var initial = manager.Refresh(7, 1);
        Assert.Contains((ulong)1, initial.Visible);
        Assert.Contains((ulong)2, initial.Entered);
        Assert.DoesNotContain((ulong)3, initial.Visible);
        Assert.Equal([1ul, 2ul], manager.GetVisibleOrdered(7));

        var unchanged = manager.Refresh(7, 1);
        Assert.Empty(unchanged.Entered);
        Assert.Empty(unchanged.Exited);
        Assert.Equal([1ul, 2ul], manager.GetVisibleOrdered(7));

        manager.Rebuild([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 24f, 0f),
            new SimulationInterestEntity(3, 21f, 0f)
        ]);
        var hysteresis = manager.Refresh(7, 1);
        Assert.Contains((ulong)2, hysteresis.Visible);
        Assert.DoesNotContain((ulong)3, hysteresis.Visible);

        manager.Rebuild([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 26f, 0f)
        ]);
        var exited = manager.Refresh(7, 1);
        Assert.Contains((ulong)2, exited.Exited);
        Assert.Equal([1ul], manager.GetVisibleOrdered(7));
    }

    [Fact]
    public void IncrementalEntityAddUpdatesOnlyNearbyExistingConnections()
    {
        var manager = new SimulationInterestManager(
            new InterestManagementConfig(10f, 20f, 25f));
        manager.Rebuild([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 100f, 0f)
        ]);
        manager.Refresh(10, 1);
        manager.Refresh(20, 2);

        var enteredConnections = manager.AddEntity(
            new SimulationInterestEntity(3, 10f, 0f));

        Assert.Equal([10], enteredConnections);
        Assert.Equal([1ul, 3ul], manager.GetVisibleOrdered(10));
        Assert.Equal([2ul], manager.GetVisibleOrdered(20));
        var unchanged = manager.Refresh(10, 1);
        Assert.Empty(unchanged.Entered);
        Assert.Empty(unchanged.Exited);
    }

    [Fact]
    public void IncrementalEntityUpdateMovesTheSpatialIndexEntry()
    {
        var manager = new SimulationInterestManager(
            new InterestManagementConfig(10f, 20f, 25f));
        manager.Rebuild([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 50f, 0f)
        ]);
        var initial = manager.Refresh(10, 1);
        Assert.DoesNotContain((ulong)2, initial.Visible);

        manager.UpdateEntity(new SimulationInterestEntity(2, 10f, 0f));
        var moved = manager.Refresh(10, 1);

        Assert.Equal([2ul], moved.Entered);
        Assert.Equal([1ul, 2ul], manager.GetVisibleOrdered(10));
    }

    [Fact]
    public void CollisionStreamingPlannerRetainsHysteresisRing()
    {
        var planner = new CollisionChunkStreamingPlanner(32f, 1, 2);
        var anchors = new[] { new SimulationVector3(1f, 0f, 1f) };

        var required = planner.GetRequiredChunks(anchors);
        var retained = planner.GetRetainedChunks(anchors);

        Assert.Equal(9, required.Count);
        Assert.Equal(25, retained.Count);
        Assert.Subset(retained, required);
    }

    [Fact]
    public void WorldCollisionStoreLoadsAndUnloadsChunksAroundActiveAnchors()
    {
        var root = Path.Combine(Path.GetTempPath(), "shooter-mmo-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sources = new List<WorldCollisionChunkSource>();
            foreach (var x in new[] { 0, 10 })
            {
                var chunk = new CollisionChunk(
                    x,
                    0,
                    new CollisionAabb(
                        new SimulationVector3(x * 32f, -1f, 0f),
                        new SimulationVector3((x + 1) * 32f, 1f, 32f)),
                    Array.Empty<CollisionBox>());
                var bytes = CollisionChunkCodec.Encode(chunk);
                var path = Path.Combine(root, $"chunk-{x}.bytes");
                File.WriteAllBytes(path, bytes);
                sources.Add(new WorldCollisionChunkSource(
                    x,
                    0,
                    $"chunk-{x}",
                    path,
                    CollisionWorldCompiler.ComputeSha256Hex(bytes)));
            }

            var store = new WorldCollisionStreamingStore(
                "test-world",
                "test-revision",
                32f,
                sources,
                new CollisionStreamingConfig(1, 1));
            store.Refresh([new SimulationVector3(1f, 0f, 1f)]);
            Assert.True(store.CollisionWorld.IsChunkLoaded(0, 0));
            Assert.False(store.CollisionWorld.IsChunkLoaded(10, 0));

            store.Refresh([new SimulationVector3(321f, 0f, 1f)]);
            Assert.False(store.CollisionWorld.IsChunkLoaded(0, 0));
            Assert.True(store.CollisionWorld.IsChunkLoaded(10, 0));
            Assert.Equal(1, store.LoadedChunkCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NetworkMetricsCaptureTransportAndQuotaCounters()
    {
        using var metrics = new RealtimeNetworkMetrics();
        metrics.SetActivePeers(2);
        metrics.SetActiveEntities(2);
        metrics.RecordReceived(120);
        metrics.RecordSent(80);
        metrics.RecordQuotaRejected();
        metrics.RecordSnapshotDropped();
        metrics.RecordSnapshotBackpressureDropped(3);
        metrics.RecordSnapshotEntities(2);

        var snapshot = metrics.Capture();
        Assert.Equal(2, snapshot.ActivePeers);
        Assert.Equal(120, snapshot.ReceivedBytes);
        Assert.Equal(80, snapshot.SentBytes);
        Assert.Equal(1, snapshot.QuotaRejectedPackets);
        Assert.Equal(4, snapshot.DroppedSnapshotPackets);
        Assert.Equal(3, snapshot.BackpressureDroppedSnapshotPackets);
        Assert.Equal(2, snapshot.SnapshotEntityRecords);
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }
}
