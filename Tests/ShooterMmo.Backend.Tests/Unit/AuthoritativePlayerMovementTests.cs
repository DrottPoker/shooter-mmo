using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using SimulationWorker.Realtime;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class AuthoritativePlayerMovementTests
{
    private static readonly MovementSimulationSettings Settings = new(
        30,
        5f,
        8f,
        720f,
        -24f,
        7f,
        -2f,
        0f,
        -14f,
        14f,
        -14f,
        14f);

    [Fact]
    public void NewestInputControlsTickAndOlderSequencesAreIgnored()
    {
        var movement = CreateMovement();
        movement.AcceptInputs(new[]
        {
            Input(1, 0f, 1f, RealtimeMovementButtons.None),
            Input(2, 1f, 0f, RealtimeMovementButtons.Sprint)
        });
        movement.AcceptInputs(new[]
        {
            Input(1, 0f, -1f, RealtimeMovementButtons.None)
        });

        movement.SimulateTick();

        Assert.Equal(2u, movement.LastProcessedInputSequence);
        Assert.True(movement.State.PositionX > 0f);
        Assert.True(movement.State.IsSprinting);
    }

    [Fact]
    public void RedundantBatchPreservesJumpEdgeUntilNextServerTick()
    {
        var movement = CreateMovement();
        movement.AcceptInputs(new[]
        {
            Input(1, 0f, 1f, RealtimeMovementButtons.Jump),
            Input(2, 0f, 1f, RealtimeMovementButtons.None)
        });

        movement.SimulateTick();

        Assert.Equal(2u, movement.LastProcessedInputSequence);
        Assert.False(movement.State.IsGrounded);
        Assert.True(movement.State.PositionY > 0f);
    }

    [Fact]
    public void StaleMovementInputIsNeutralizedUntilANewerInputArrives()
    {
        var movement = CreateMovement(maximumInputSilenceTicks: 2);
        movement.AcceptInputs(new[]
        {
            Input(1, 0f, 1f, RealtimeMovementButtons.Sprint)
        });

        movement.SimulateTick();
        movement.SimulateTick();
        var positionBeforeTimeout = movement.State.PositionZ;
        movement.SimulateTick();

        Assert.Equal(positionBeforeTimeout, movement.State.PositionZ);
        Assert.False(movement.State.IsSprinting);

        movement.AcceptInputs(new[]
        {
            Input(2, 0f, 1f, RealtimeMovementButtons.None)
        });
        movement.SimulateTick();

        Assert.True(movement.State.PositionZ > positionBeforeTimeout);
        Assert.Equal(2u, movement.LastProcessedInputSequence);
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
            collisionWorld,
            15);

        for (var tick = 0; tick < 20; tick++)
        {
            movement.SimulateTick();
        }

        Assert.Equal(1, collisionWorld.ObservedBufferCount);
    }

    private static AuthoritativePlayerMovement CreateMovement(int maximumInputSilenceTicks = 15)
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
            collisionWorld,
            maximumInputSilenceTicks);
    }

    private static RealtimeMovementInput Input(
        uint sequence,
        float moveX,
        float moveY,
        RealtimeMovementButtons buttons)
    {
        return new RealtimeMovementInput(sequence, sequence, moveX, moveY, 0f, buttons);
    }

    private sealed class RecordingCollisionWorld(ICollisionWorld inner) : ICollisionWorld
    {
        private readonly HashSet<CollisionQueryBuffer> observedBuffers = [];

        public int ObservedBufferCount => observedBuffers.Count;

        public void QueryBoxes(
            CollisionAabb bounds,
            uint layerMask,
            CollisionQueryBuffer buffer)
        {
            observedBuffers.Add(buffer);
            inner.QueryBoxes(bounds, layerMask, buffer);
        }

        public void ResetObservedBuffers()
        {
            observedBuffers.Clear();
        }
    }
}
