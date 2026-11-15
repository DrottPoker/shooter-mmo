using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Collision chunks load on the store's background loader, never on the loop: a mover whose chunks
// still load waits, and a join or an admin teleport to a place whose chunks still load completes
// once they are in.
public sealed class RealtimeCollisionStreamingTests
{
    private const string LoopThreadName = "SimulationWorker realtime loop";
    private static readonly SimulationVector3 Spawn = new(0f, 0f, -1f);

    // A player whose chunks still load does not step and is counted; the player whose chunks are
    // in falls as usual, and the waiting one falls once its chunks arrived.
    [Fact]
    public async Task AMoverWhoseChunksStillLoadWaitsAndTheOthersStep()
    {
        using var streamed = new StreamedTestCollision(Spawn);
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            StreamedCollision = streamed,
            ProvideMetrics = true
        });
        var server = harness.Service.Server;
        var near = RegisterFallingPlayer(harness, "Near Faller", 0f, -1f);
        var far = StreamedTestCollision.FarPlace;
        var away = RegisterFallingPlayer(harness, "Far Faller", far.X, far.Z);
        var phases = new SimulationTickPhases();
        foreach (var phase in server.TickPhases.Where(phase => phase.Name is "collision streaming" or "movement"))
        {
            phases.AddPeriodic(phase.Order, phase.Name, phase.Run);
        }

        var loop = SimulationTickLoopTests.CreateLoop(
            new RealtimeSimulationClock(),
            harness.EntityRegistry,
            phases,
            harness.PerformanceMetrics!,
            out var guard);
        var nextTick = TimeSpan.Zero;
        streamed.HoldReads();

        // The harness is not started, so this thread is the loop; nothing below awaits, so it
        // stays the loop. The reads so far loaded the spawn before the loop started.
        var loopThread = Environment.CurrentManagedThreadId;
        var readsBeforeStart = streamed.Reads.Count;
        SimulationTickLoopTests.RunTicks(loop, ref nextTick, 5);

        Assert.True(near.Movement.State.PositionY < 3f);
        Assert.Equal(3f, away.Movement.State.PositionY);
        Assert.True(streamed.Store.LoadingChunkCount > 0);
        Assert.Equal(5, harness.PerformanceMetrics!.CaptureAndReset().CollisionPendingSteps);

        streamed.ReleaseReads();
        using var timeout = RealtimeServerHarness.CreateTimeout();
        while (away.Movement.State.PositionY == 3f)
        {
            timeout.Token.ThrowIfCancellationRequested();
            Thread.Sleep(1);
            SimulationTickLoopTests.RunTicks(loop, ref nextTick, 1);
        }

        var loopReads = streamed.Reads.Skip(readsBeforeStart).ToArray();
        Assert.Contains(loopReads, read => read.X == 3 && read.Z == 3);
        Assert.DoesNotContain(loopReads, read => read.ThreadId == loopThread);
        Assert.Equal(0, guard.TotalFailures);
    }

    // A player whose saved place lies far from every player joins only once the chunks of that
    // place are in, and the loop read none of them.
    [Fact]
    public async Task AJoinFarFromEveryPlayerIsPlacedOnlyOnceItsChunksAreIn()
    {
        using var streamed = new StreamedTestCollision(Spawn);
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            StreamedCollision = streamed
        });
        var far = StreamedTestCollision.FarPlace;
        var traveller = harness.AddCharacter("Far Traveller") with
        {
            SavedState = new SimulationSavedCharacterState(
                RealtimeServerHarness.WorldId,
                harness.StaticCollisionWorld.Revision,
                far.X,
                far.Y,
                far.Z,
                90f,
                40,
                10)
        };
        harness.GameService.AddCharacter(traveller);
        await harness.StartAsync();
        streamed.HoldReads();

        var client = harness.Connect(traveller.Name, traveller.Ticket);
        await harness.PollUntilAsync(
            () => harness.SessionStore.ListActiveSessions().Count == 1,
            "the ticket to be consumed");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
        Assert.Null(client.JoinAccepted);
        Assert.Equal(0, harness.EntityRegistry.PlayerCount);

        streamed.ReleaseReads();
        await harness.PollUntilAsync(() => client.JoinAccepted is not null, "the far join");

        var placed = client.JoinAccepted!.InitialPlayerState;
        Assert.Equal(far.X, placed.PositionX, 3);
        Assert.Equal(0f, placed.PositionY, 3);
        Assert.Equal(far.Z, placed.PositionZ, 3);
        Assert.True(placed.IsGrounded);
        Assert.Contains(streamed.Reads, read => read.X == 3 && read.Z == 3);
        Assert.DoesNotContain(streamed.Reads, read => read.ThreadName == LoopThreadName);
    }

    // An admin teleport to a place whose chunks are not loaded answers once they are in, and the
    // player stays where it was until then.
    [Fact]
    public async Task AnAdminTeleportFarAwayCompletesOnceItsChunksAreIn()
    {
        using var streamed = new StreamedTestCollision(Spawn);
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            StreamedCollision = streamed,
            EnableAdminTools = true
        });
        var traveller = harness.AddCharacter("Teleport Traveller");
        await harness.StartAsync();
        var owner = await harness.JoinAsync(traveller);
        streamed.HoldReads();

        var far = StreamedTestCollision.FarPlace;
        var teleport = RealtimeAdminIntent.CreateTeleport(Guid.NewGuid(), far.X, 0.05f, far.Z);
        owner.Send(RealtimeProtocol.EncodeAdminIntent(teleport));
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
        Assert.Empty(Results(owner));
        Assert.All(owner.OwnerSnapshots, snapshot => Assert.True(snapshot.State.PositionX < 1f));

        streamed.ReleaseReads();
        await harness.PollUntilAsync(
            () => Results(owner).Count == 1
                && owner.OwnerSnapshots.Any(snapshot => Math.Abs(snapshot.State.PositionX - far.X) < 0.001f),
            "the far teleport");

        var result = Assert.Single(Results(owner));
        Assert.True(result.Succeeded);
        Assert.Equal(teleport.OperationId, result.OperationId);
        var arrived = owner.OwnerSnapshots.Last().State;
        Assert.Equal(far.Z, arrived.PositionZ, 3);
        Assert.Equal(0f, arrived.PositionY, 3);
        Assert.Contains(streamed.Reads, read => read.X == 3 && read.Z == 3);
        Assert.DoesNotContain(streamed.Reads, read => read.ThreadName == LoopThreadName);
    }

    // A teleport that waits for its chunks must not run after a newer teleport that completed
    // meanwhile, or the character would end where the player asked first.
    [Fact]
    public async Task ANewerTeleportReplacesOneThatStillWaits()
    {
        using var streamed = new StreamedTestCollision(Spawn);
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            StreamedCollision = streamed,
            EnableAdminTools = true
        });
        var traveller = harness.AddCharacter("Twice Teleported");
        await harness.StartAsync();
        var owner = await harness.JoinAsync(traveller);
        streamed.HoldReads();

        var far = StreamedTestCollision.FarPlace;
        var first = RealtimeAdminIntent.CreateTeleport(Guid.NewGuid(), far.X, 0.05f, far.Z);
        owner.Send(RealtimeProtocol.EncodeAdminIntent(first));
        await harness.PollForAsync(TimeSpan.FromMilliseconds(200));
        Assert.Empty(Results(owner));

        // The second destination lies beside the spawn, where the chunks are loaded.
        var second = RealtimeAdminIntent.CreateTeleport(Guid.NewGuid(), 2f, 0.05f, 1f);
        owner.Send(RealtimeProtocol.EncodeAdminIntent(second));
        await harness.PollUntilAsync(() => Results(owner).Count == 1, "the newer teleport");
        Assert.True(Results(owner)[0].Succeeded);
        Assert.Equal(second.OperationId, Results(owner)[0].OperationId);

        streamed.ReleaseReads();
        await harness.PollUntilAsync(() => Results(owner).Count == 2, "the answer to the replaced teleport");
        var replaced = Results(owner)[1];
        Assert.Equal(first.OperationId, replaced.OperationId);
        Assert.False(replaced.Succeeded);
        Assert.Equal("admin_teleport_superseded", replaced.Error!.Code);

        await harness.PollForAsync(TimeSpan.FromMilliseconds(200));
        var state = owner.OwnerSnapshots.Last().State;
        Assert.Equal(2f, state.PositionX, 3);
        Assert.Equal(1f, state.PositionZ, 3);

        // A teleport after the replaced one waits and completes as usual.
        streamed.HoldReads();
        var third = RealtimeAdminIntent.CreateTeleport(Guid.NewGuid(), -far.X, 0.05f, -far.Z);
        owner.Send(RealtimeProtocol.EncodeAdminIntent(third));
        await harness.PollForAsync(TimeSpan.FromMilliseconds(200));
        Assert.Equal(2, Results(owner).Count);
        streamed.ReleaseReads();
        await harness.PollUntilAsync(() => Results(owner).Count == 3, "the third teleport");
        Assert.True(Results(owner)[2].Succeeded);
        Assert.Equal(third.OperationId, Results(owner)[2].OperationId);
    }

    // A required chunk whose file is broken fails every refresh while it is required, with the
    // message of the failed check, and is tried again each time.
    [Fact]
    public void AChunkWithAWrongHashFailsEveryRefreshWhileItIsRequired()
    {
        var root = Path.Combine(Path.GetTempPath(), "shooter-mmo-broken-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var chunk = new CollisionChunk(
                0,
                0,
                new CollisionAabb(new SimulationVector3(0f, -1f, 0f), new SimulationVector3(32f, 1f, 32f)),
                Array.Empty<CollisionBox>());
            var path = Path.Combine(root, "chunk-0.bytes");
            File.WriteAllBytes(path, CollisionChunkCodec.Encode(chunk));
            var reads = 0;
            using var store = new WorldCollisionStreamingStore(
                "test-world",
                "test-revision",
                32f,
                [new WorldCollisionChunkSource(0, 0, "chunk-0", path, new string('0', 64))],
                new CollisionStreamingConfig(1, 1),
                source =>
                {
                    Interlocked.Increment(ref reads);
                    return WorldCollisionChunkReader.Read(source);
                });
            var anchor = new SimulationVector3(1f, 0f, 1f);

            var failures = 0;
            using var timeout = RealtimeServerHarness.CreateTimeout();
            while (failures < 3 || Volatile.Read(ref reads) < 3)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    store.Refresh([anchor]);
                    Thread.Sleep(1);
                }
                catch (InvalidOperationException exception)
                {
                    Assert.Equal("Collision chunk 'chunk-0' failed checksum validation while streaming.", exception.Message);
                    failures++;
                }
            }

            Assert.False(store.IsAreaLoaded(anchor));
            Assert.Equal(0, store.LoadedChunkCount);

            // Once nothing needs the chunk, its failure no longer counts.
            store.Refresh([new SimulationVector3(321f, 0f, 1f)]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Between the tick that releases a waiting completion and the pass of the completion queue
    // that finalizes it, ticks may run. They must not unload the place again, or the completion
    // would wait for its chunks once more, and again after that.
    [Fact]
    public void AReleasedPlaceStaysLoadedUntilTheCompletionQueuePassedIt()
    {
        using var streamed = new StreamedTestCollision(Spawn);
        using var metrics = new RealtimePerformanceMetrics();
        var completions = new RealtimeCompletionQueue();
        var gate = new CollisionChunkGate(streamed.Store, completions, metrics);
        var handler = new RecordingCompletionHandler();
        var context = new RealtimePeerContext(1, DateTime.UtcNow, UdpQuotaConfig.Default);
        var far = StreamedTestCollision.FarPlace;
        var waiting = new AdminTeleportReady(context, 1, RealtimeAdminIntent.CreateTeleport(Guid.NewGuid(), far.X, 0f, far.Z));
        var anchors = new List<SimulationVector3>();

        void Tick()
        {
            anchors.Clear();
            anchors.Add(Spawn);
            gate.AddAnchorsTo(anchors);
            streamed.Store.Refresh(anchors);
            gate.ReleaseLoadedPlaces();
        }

        Assert.False(gate.IsLoadedAround(far));
        gate.WaitFor(far, handler, waiting);
        using var timeout = RealtimeServerHarness.CreateTimeout();
        while (completions.Count == 0)
        {
            timeout.Token.ThrowIfCancellationRequested();
            Tick();
            Thread.Sleep(1);
        }

        Assert.Equal(0, gate.WaitingCount);
        Assert.Equal(1, gate.HeldCount);

        // Ticks before the completion queue comes by keep the place loaded.
        Tick();
        Tick();
        Assert.True(gate.IsLoadedAround(far));

        Assert.True(completions.TryDequeue(out var released));
        Assert.Same(handler, released.Handler);
        Assert.Same(waiting, released.Operation);
        released.Handler.Complete(released.Operation);
        Assert.Equal(1, gate.HeldCount);
        Assert.True(completions.TryDequeue(out var ended));
        ended.Handler.Complete(ended.Operation);
        Assert.Equal(0, gate.HeldCount);
        Assert.Equal(0, completions.Count);
        Assert.Same(waiting, Assert.Single(handler.Completed));

        // Nothing stands there, so the next tick lets the chunks go.
        Tick();
        Assert.False(gate.IsLoadedAround(far));
    }

    private sealed class RecordingCompletionHandler : IRealtimeCompletionHandler
    {
        public List<RealtimeOperationResult> Completed { get; } = [];

        public void Complete(RealtimeOperationResult completion)
        {
            Completed.Add(completion);
        }
    }

    // A player registered on the streamed ground and put in the air, where it falls once it may step.
    private static PlayerSimulationEntity RegisterFallingPlayer(RealtimeServerHarness harness, string name, float x, float z)
    {
        var config = harness.Config;
        var session = RealtimeTestSessions.Create(name);
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, harness.SessionStore.Register(session));
        var entity = harness.EntityRegistry.RegisterPlayer(
                session,
                () => new AuthoritativePlayerMovement(
                    new PlayerMovementState(x, 3f, z, 0f, 0f, 0f, 0f, false, MovementPathProgress.None),
                    config.MovementSimulation,
                    session.CarryState,
                    harness.CollisionWorld))
            .Entity;
        entity.Movement.Teleport(entity.Movement.State);
        return entity;
    }

    private static IReadOnlyList<RealtimeAdminIntentResult> Results(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeAdminIntentResult>(
            RealtimeMessageType.AdminIntentResult,
            RealtimeProtocol.TryDecodeAdminIntentResult);
    }
}
