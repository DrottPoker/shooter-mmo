using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using WorldServer.Realtime;

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
            collisionWorld);
    }

    private static RealtimeMovementInput Input(
        uint sequence,
        float moveX,
        float moveY,
        RealtimeMovementButtons buttons)
    {
        return new RealtimeMovementInput(sequence, sequence, moveX, moveY, 0f, buttons);
    }
}
