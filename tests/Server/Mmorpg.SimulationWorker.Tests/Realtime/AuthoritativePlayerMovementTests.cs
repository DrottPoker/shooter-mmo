using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.Testing.Content;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class AuthoritativePlayerMovementTests
{
    private static readonly MovementSimulationSettings Settings = new(
        30,
        5f,
        720f,
        -24f,
        -2f,
        0f,
        -14f,
        14f,
        -14f,
        14f);

    [Fact]
    public void NewestInputControlsTheTickAndOlderSequencesAreIgnored()
    {
        var movement = CreateMovement();
        Assert.Equal(
            MovementInputAcceptance.Accepted,
            movement.AcceptInputs(Batch(Path(2, (5f, -1f)), Input(1, 1), Input(2, 2))));
        Assert.Equal(
            MovementInputAcceptance.Accepted,
            movement.AcceptInputs(Batch(Path(1, (-5f, -1f)), Input(1, 1))));

        movement.SimulateTick();

        Assert.Equal(2u, movement.LastProcessedInputSequence);
        Assert.Equal(2u, movement.CurrentPath!.Id);
        Assert.True(movement.State.PositionX > 0f);
        Assert.Equal(new MovementPathProgress(2, 0, 0), movement.State.PathProgress);
    }

    [Fact]
    public void KnownPathContinuesWithoutItsWaypointsAndPathIdZeroStands()
    {
        var movement = CreateMovement();
        movement.AcceptInputs(Batch(Path(3, (0f, 5f)), Input(1, 3)));
        movement.SimulateTick();

        Assert.Equal(MovementInputAcceptance.Accepted, movement.AcceptInputs(Batch(null, Input(2, 3))));
        movement.SimulateTick();
        Assert.Equal(new MovementPathProgress(3, 0, 0), movement.State.PathProgress);
        var moved = movement.State.PositionZ;
        Assert.True(moved > -1f);

        Assert.Equal(MovementInputAcceptance.Accepted, movement.AcceptInputs(Batch(null, Input(3, 0))));
        movement.SimulateTick();
        Assert.Equal(MovementPathProgress.None, movement.State.PathProgress);
        Assert.Equal(moved, movement.State.PositionZ);
    }

    [Fact]
    public void UnknownPathIsReportedWithoutChangingTheIntent()
    {
        var movement = CreateMovement();
        movement.AcceptInputs(Batch(Path(3, (0f, 5f)), Input(1, 3)));

        Assert.Equal(MovementInputAcceptance.UnknownPath, movement.AcceptInputs(Batch(null, Input(2, 4))));
        Assert.Equal(1u, movement.LastReceivedInputSequence);
        Assert.Equal(3u, movement.CurrentPath!.Id);
    }

    [Fact]
    public void PathOutsideTheWorldIsAdoptedAsAlreadyEnded()
    {
        var movement = CreateMovement();
        movement.AcceptInputs(Batch(Path(1, (0f, 5f)), Input(1, 1)));
        movement.SimulateTick();
        var stoppedAt = movement.State.PositionZ;

        Assert.Equal(
            MovementInputAcceptance.PathRejected,
            movement.AcceptInputs(Batch(Path(2, (0f, 5f), (20f, 5f)), Input(2, 2))));
        movement.SimulateTick();

        // The worker and the client agree that the character stands at the end of path 2,
        // and later inputs that still name it stay valid.
        Assert.Equal(new MovementPathProgress(2, 2, 0), movement.State.PathProgress);
        Assert.Equal(stoppedAt, movement.State.PositionZ);
        Assert.Equal(MovementInputAcceptance.Accepted, movement.AcceptInputs(Batch(null, Input(3, 2))));
    }

    [Fact]
    public void PathContinuesWhileInputsArePausedBecauseEveryPathEnds()
    {
        var movement = CreateMovement();
        movement.AcceptInputs(Batch(Path(1, (0f, 1f)), Input(1, 1)));

        for (var tick = 0; tick < 30; tick++)
        {
            movement.SimulateTick();
        }

        Assert.Equal(new MovementPathProgress(1, 1, 0), movement.State.PathProgress);
        Assert.Equal(1f, movement.State.PositionZ, 4);
    }

    [Fact]
    public void ReconnectedClientStartsInputSequencesAndPathIdsFromTheBeginning()
    {
        var movement = CreateMovement();
        movement.AcceptInputs(Batch(Path(1, (0f, 5f)), Input(5_000, 1)));
        movement.SimulateTick();

        movement.ResetInputSequence();
        Assert.Equal(MovementPathProgress.None, movement.State.PathProgress);
        Assert.Null(movement.CurrentPath);

        // The new client generation reuses path id 1 for another route.
        movement.AcceptInputs(Batch(Path(1, (5f, -1f)), Input(1, 1)));
        var positionBefore = movement.State.PositionX;
        movement.SimulateTick();

        Assert.Equal(1u, movement.LastReceivedInputSequence);
        Assert.Equal(1u, movement.LastProcessedInputSequence);
        Assert.True(movement.State.PositionX > positionBefore);
    }

    [Fact]
    public void SimulationTicksReuseOneCollisionQueryBuffer()
    {
        var collisionWorld = new RecordingCollisionWorld(
            CollisionTestWorldFactory.Create());
        var initialState = PlayerMovementSimulation.CreateInitialState(
            Settings,
            collisionWorld,
            0f,
            0f,
            -1f,
            0f);
        collisionWorld.ResetObservedBuffers();
        var movement = new AuthoritativePlayerMovement(
            initialState,
            Settings,
            PlayerCarryState.Default,
            collisionWorld);
        movement.AcceptInputs(Batch(Path(1, (3f, 3f)), Input(1, 1)));

        for (var tick = 0; tick < 20; tick++)
        {
            movement.SimulateTick();
        }

        Assert.Equal(1, collisionWorld.ObservedBufferCount);
    }

    [Fact]
    public void CommittedCarryRevisionChangesAuthoritativeSpeed()
    {
        var movement = CreateMovement();
        movement.AcceptInputs(Batch(Path(1, (0f, 10f)), Input(1, 1)));

        Assert.True(movement.ApplyCarryState(new PlayerCarryState(1, 210, 200)));
        movement.SimulateTick();

        Assert.Equal(Settings.WalkSpeed * 0.9f, movement.State.VelocityZ, 4);
        Assert.False(movement.ApplyCarryState(new PlayerCarryState(1, 210, 200)));
        Assert.Throws<InvalidOperationException>(() =>
            movement.ApplyCarryState(new PlayerCarryState(0, 0, 200)));
        Assert.Throws<InvalidOperationException>(() =>
            movement.ApplyCarryState(new PlayerCarryState(1, 200, 200)));
    }

    private static AuthoritativePlayerMovement CreateMovement()
    {
        var collisionWorld = CollisionTestWorldFactory.Create();
        return new AuthoritativePlayerMovement(
            PlayerMovementSimulation.CreateInitialState(
                Settings,
                collisionWorld,
                0f,
                0f,
                -1f,
                0f),
            Settings,
            PlayerCarryState.Default,
            collisionWorld);
    }

    private static RealtimeMovementInput Input(uint sequence, uint pathId)
    {
        return new RealtimeMovementInput(sequence, sequence, pathId);
    }

    private static RealtimeMovementPath Path(uint id, params (float X, float Z)[] waypoints)
    {
        return new RealtimeMovementPath(
            id,
            waypoints.Select(waypoint => new RealtimeMovementPathWaypoint(waypoint.X, 0f, waypoint.Z)).ToArray());
    }

    private static RealtimeMovementInputBatch Batch(
        RealtimeMovementPath? path,
        params RealtimeMovementInput[] inputs)
    {
        return new RealtimeMovementInputBatch(inputs, path);
    }

    private sealed class RecordingCollisionWorld(ICollisionWorld inner) : ICollisionWorld
    {
        private readonly HashSet<CollisionQueryBuffer> observedBuffers = [];

        public int ObservedBufferCount => observedBuffers.Count;

        public void QueryShapes(
            CollisionAabb bounds,
            uint layerMask,
            CollisionQueryBuffer buffer)
        {
            observedBuffers.Add(buffer);
            inner.QueryShapes(bounds, layerMask, buffer);
        }

        public void ResetObservedBuffers()
        {
            observedBuffers.Clear();
        }
    }
}
