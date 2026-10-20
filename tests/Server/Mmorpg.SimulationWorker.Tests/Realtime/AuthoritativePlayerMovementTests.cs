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
    public void InputsStepOncePerTickInSequenceOrderAndOlderSequencesAreIgnored()
    {
        var movement = CreateMovement();
        Assert.Equal(
            MovementInputAcceptance.Accepted,
            movement.AcceptInputs(Batch(Path(2, (5f, -1f)), Input(1, 2), Input(2, 2))));
        Assert.Equal(
            MovementInputAcceptance.Accepted,
            movement.AcceptInputs(Batch(Path(1, (-5f, -1f)), Input(1, 1))));
        Assert.Equal(2u, movement.LastReceivedInputSequence);

        movement.SimulateTick();
        Assert.Equal(1u, movement.LastProcessedInputSequence);
        var afterOneStep = movement.State.PositionX;
        Assert.True(afterOneStep > 0f);

        movement.SimulateTick();
        Assert.Equal(2u, movement.LastProcessedInputSequence);
        Assert.Equal(2u, movement.CurrentPath!.Id);
        Assert.True(movement.State.PositionX > afterOneStep);
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
        movement.SimulateTick();

        Assert.Equal(MovementInputAcceptance.UnknownPath, movement.AcceptInputs(Batch(null, Input(2, 4))));
        Assert.Equal(1u, movement.LastReceivedInputSequence);
        Assert.Equal(0, movement.QueuedInputCount);
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
    public void UnevenArrivalNeverMovesTheWorkerAwayFromThePrediction()
    {
        var movement = CreateMovement();
        var path = Path(1, (0f, 12f));
        var expected = PredictedStates(path, 60 + AuthoritativePlayerMovement.MaximumRunAheadInputs);

        // Inputs arrive in bursts and gaps, as packets do, but every acknowledged state must
        // equal the state the client predicted for that input, including the steps taken
        // ahead of a late input.
        int[] arrivalsPerTick = [2, 0, 1, 3, 0, 0, 2, 1, 0, 2, 2, 0, 1, 1, 0, 3, 0, 1, 2, 0];
        uint sent = 0;
        for (var round = 0; round < 3; round++)
        {
            foreach (var arrivals in arrivalsPerTick)
            {
                for (var index = 0; index < arrivals && sent < 60; index++)
                {
                    sent++;
                    movement.AcceptInputs(Batch(path, RedundantInputs(sent, 1)));
                }

                movement.SimulateTick();
                var processed = (int)movement.LastProcessedInputSequence;
                AssertState(expected[processed], movement.ConfirmedState);
                AssertState(expected[processed + movement.RunAheadInputCount], movement.State);
            }
        }

        while (movement.QueuedInputCount > 0)
        {
            movement.SimulateTick();
        }

        Assert.Equal(60u, movement.LastProcessedInputSequence);
        AssertState(expected[60], movement.ConfirmedState);
    }

    [Fact]
    public void LateInputsAreSteppedAheadThenTheCharacterPausesAndCatchesUp()
    {
        var movement = CreateMovement();
        var path = Path(1, (0f, 12f));
        var expected = PredictedStates(path, 20);
        movement.AcceptInputs(Batch(path, Input(1, 1)));

        for (var tick = 0; tick < 10; tick++)
        {
            movement.SimulateTick();
        }

        // One input plus the steps taken ahead for three late ones, then a pause. The owner
        // still sees the state after its only input.
        Assert.Equal(1u, movement.LastProcessedInputSequence);
        Assert.Equal(AuthoritativePlayerMovement.MaximumRunAheadInputs, movement.RunAheadInputCount);
        AssertState(expected[1], movement.ConfirmedState);
        AssertState(expected[4], movement.State);

        for (uint sequence = 2; sequence <= 12; sequence++)
        {
            movement.AcceptInputs(Batch(path, RedundantInputs(sequence, 1)));
        }

        // The late inputs 2 to 4 take over the steps taken ahead, and the paused ticks let
        // the queue drain at two steps per tick.
        Assert.Equal(4u, movement.LastProcessedInputSequence);
        AssertState(expected[4], movement.ConfirmedState);
        Assert.Equal(8, movement.QueuedInputCount);
        movement.SimulateTick();
        Assert.Equal(6u, movement.LastProcessedInputSequence);
        for (var tick = 0; tick < 4; tick++)
        {
            movement.SimulateTick();
        }

        Assert.Equal(12u, movement.LastProcessedInputSequence);
        AssertState(expected[12], movement.State);
    }

    [Fact]
    public void LostInputsAreFilledWithThePathBeforeThem()
    {
        var movement = CreateMovement();
        var path = Path(1, (0f, 12f));
        var expected = PredictedStates(path, 12);
        movement.AcceptInputs(Batch(path, Input(1, 1)));
        movement.SimulateTick();

        // Inputs 2 to 7 never arrive, not even inside a later batch, and the input after
        // them arrives before the worker steps ahead.
        movement.AcceptInputs(Batch(null, Input(8, 1)));
        Assert.Equal(7, movement.QueuedInputCount);
        while (movement.QueuedInputCount > 0)
        {
            movement.SimulateTick();
        }

        Assert.Equal(8u, movement.LastProcessedInputSequence);
        AssertState(expected[8], movement.ConfirmedState);
    }

    [Fact]
    public void LateInputWithAnotherPathReplacesTheStepsTakenAhead()
    {
        var movement = CreateMovement();
        var straight = Path(1, (0f, 12f));
        var turn = Path(2, (8f, -1f));
        movement.AcceptInputs(Batch(straight, Input(1, 1)));
        movement.SimulateTick();
        movement.SimulateTick();
        movement.SimulateTick();
        Assert.Equal(2, movement.RunAheadInputCount);

        // Input 2 already turned, so the two steps along the straight path are taken again.
        movement.AcceptInputs(Batch(turn, Input(2, 2)));

        var collisionWorld = CollisionTestWorldFactory.Create();
        var afterOne = PredictedStates(straight, 1)[1];
        var turnPath = new MovementPath(2, [new SimulationVector3(8f, 0f, -1f)]);
        var afterTwo = PlayerMovementSimulation.Step(
            afterOne,
            new PlayerMovementInput(2, 2, turnPath),
            Settings,
            PlayerCarryState.Default,
            collisionWorld);
        var afterThree = PlayerMovementSimulation.Step(
            afterTwo,
            new PlayerMovementInput(3, 3, turnPath),
            Settings,
            PlayerCarryState.Default,
            collisionWorld);
        Assert.Equal(2u, movement.LastProcessedInputSequence);
        Assert.Equal(1, movement.RunAheadInputCount);
        AssertState(afterTwo, movement.ConfirmedState);
        AssertState(afterThree, movement.State);
        Assert.Equal(2u, movement.CurrentPath!.Id);
    }

    [Fact]
    public void GapLongerThanTheQueueSkipsAheadToTheReceivedInput()
    {
        var movement = CreateMovement();
        movement.AcceptInputs(Batch(Path(1, (0f, 5f)), Input(1, 1)));
        movement.SimulateTick();

        movement.AcceptInputs(Batch(null, Input(500, 1)));

        Assert.Equal(1, movement.QueuedInputCount);
        movement.SimulateTick();
        Assert.Equal(500u, movement.LastProcessedInputSequence);
    }

    [Fact]
    public void FloodedInputsCannotMoveTheCharacterFasterThanOneStepPerTick()
    {
        var movement = CreateMovement();
        // Fourteen meters take 84 honest steps, more than the ticks below allow.
        var path = Path(1, (0f, 13f));
        uint sent = 0;
        const int ticks = 60;
        for (var tick = 0; tick < ticks; tick++)
        {
            // A cheating client sends four new inputs per tick.
            for (var index = 0; index < 4; index++)
            {
                sent++;
                movement.AcceptInputs(Batch(path, RedundantInputs(sent, 1)));
            }

            movement.SimulateTick();
        }

        var allowedSteps = (ticks * 65 / 64) + AuthoritativePlayerMovement.MaximumStepBudget + 1;
        Assert.True(
            movement.State.PositionZ + 1f <= Settings.WalkSpeed * allowedSteps / Settings.TickRateHz,
            $"The character walked {movement.State.PositionZ + 1f} m in {ticks} ticks.");
        Assert.True(movement.QueuedInputCount <= AuthoritativePlayerMovement.MaximumQueuedInputs);
    }

    [Fact]
    public void HaltDiscardsWaitingInputsAndKeepsThePhysics()
    {
        var movement = CreateMovement();
        var path = Path(1, (0f, 12f));
        movement.AcceptInputs(Batch(path, Input(1, 1), Input(2, 1), Input(3, 1)));
        movement.SimulateTick();
        var haltedAt = movement.State.PositionZ;

        movement.Halt();

        Assert.Equal(0, movement.QueuedInputCount);
        Assert.Equal(3u, movement.LastProcessedInputSequence);
        for (var tick = 0; tick < 10; tick++)
        {
            movement.SimulateTick();
        }

        Assert.Equal(new MovementPathProgress(1, 1, 0), movement.State.PathProgress);
        Assert.Equal(haltedAt, movement.State.PositionZ, 3);
        Assert.True(movement.IsStandingStill);
        Assert.Equal(3u, movement.LastProcessedInputSequence);
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

        for (uint sequence = 1; sequence <= 20; sequence++)
        {
            movement.AcceptInputs(Batch(Path(1, (3f, 3f)), Input(sequence, 1)));
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

    // The client's prediction: one step per input along the path, from the same start.
    private static PlayerMovementState[] PredictedStates(RealtimeMovementPath wirePath, int count)
    {
        var collisionWorld = CollisionTestWorldFactory.Create();
        var path = new MovementPath(
            wirePath.PathId,
            wirePath.Waypoints
                .Select(waypoint => new SimulationVector3(waypoint.X, waypoint.Y, waypoint.Z))
                .ToArray());
        var states = new PlayerMovementState[count + 1];
        states[0] = PlayerMovementSimulation.CreateInitialState(Settings, collisionWorld, 0f, 0f, -1f, 0f);
        for (var index = 1; index <= count; index++)
        {
            states[index] = PlayerMovementSimulation.Step(
                states[index - 1],
                new PlayerMovementInput((uint)index, (uint)index, path),
                Settings,
                PlayerCarryState.Default,
                collisionWorld);
        }

        return states;
    }

    // The newest input and up to three before it, as the client sends them.
    private static RealtimeMovementInput[] RedundantInputs(uint newestSequence, uint pathId)
    {
        var first = newestSequence > RealtimeProtocol.MaximumInputBatchSize
            ? newestSequence - (RealtimeProtocol.MaximumInputBatchSize - 1)
            : 1u;
        var inputs = new List<RealtimeMovementInput>();
        for (var sequence = first; sequence <= newestSequence; sequence++)
        {
            inputs.Add(Input(sequence, pathId));
        }

        return inputs.ToArray();
    }

    private static void AssertState(PlayerMovementState expected, PlayerMovementState actual)
    {
        Assert.Equal(expected.PositionX, actual.PositionX, 5);
        Assert.Equal(expected.PositionY, actual.PositionY, 5);
        Assert.Equal(expected.PositionZ, actual.PositionZ, 5);
        Assert.Equal(expected.PathProgress, actual.PathProgress);
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
