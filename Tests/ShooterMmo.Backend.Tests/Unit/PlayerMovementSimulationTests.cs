using ShooterMmo.GameSimulation;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class PlayerMovementSimulationTests
{
    private static readonly ChunkedStaticCollisionWorld CollisionWorld =
        CollisionTestWorldFactory.Create();
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
    public void FixedStepMovesRelativeToCameraYaw()
    {
        var initial = CreateInitialState(0f, 0f, 0f);
        var input = new PlayerMovementInput(
            1,
            1,
            0f,
            1f,
            90f,
            PlayerMovementButtons.None);

        var result = PlayerMovementSimulation.Step(initial, input, Settings, CollisionWorld);

        Assert.True(result.PositionX > 0f);
        Assert.InRange(Math.Abs(result.PositionZ), 0f, 0.0001f);
        Assert.InRange(result.YawDegrees, 23.999f, 24.001f);
    }

    [Fact]
    public void SprintCannotStartWhileAirborne()
    {
        var airborne = new PlayerMovementState(
            0f,
            2f,
            0f,
            0f,
            1f,
            0f,
            0f,
            false,
            false);
        var input = new PlayerMovementInput(
            1,
            1,
            0f,
            1f,
            0f,
            PlayerMovementButtons.Sprint);

        var result = PlayerMovementSimulation.Step(airborne, input, Settings, CollisionWorld);

        Assert.False(result.IsSprinting);
        Assert.Equal(Settings.WalkSpeed, result.VelocityZ);
    }

    [Fact]
    public void SprintPersistsThroughJumpUntilReleased()
    {
        var grounded = CreateInitialState(0f, 0f, 0f);
        var sprint = new PlayerMovementInput(
            1,
            1,
            0f,
            1f,
            0f,
            PlayerMovementButtons.Sprint);
        var jump = new PlayerMovementInput(
            2,
            2,
            0f,
            1f,
            0f,
            PlayerMovementButtons.Sprint | PlayerMovementButtons.Jump);

        var sprinting = PlayerMovementSimulation.Step(grounded, sprint, Settings, CollisionWorld);
        var airborne = PlayerMovementSimulation.Step(sprinting, jump, Settings, CollisionWorld);

        Assert.True(airborne.IsSprinting);
        Assert.False(airborne.IsGrounded);
        Assert.Equal(Settings.SprintSpeed, airborne.VelocityZ);
    }

    [Fact]
    public void WorldBoundsClampAuthoritativePosition()
    {
        var initial = CreateInitialState(13.95f, 0f, 0f);
        var input = new PlayerMovementInput(
            1,
            1,
            1f,
            0f,
            0f,
            PlayerMovementButtons.Sprint);

        var result = PlayerMovementSimulation.Step(initial, input, Settings, CollisionWorld);

        Assert.Equal(Settings.MaximumX, result.PositionX);
    }

    [Fact]
    public void FallingVelocityIsClampedToTheConfiguredTerminalSpeed()
    {
        var airborne = new PlayerMovementState(
            0f,
            100f,
            0f,
            0f,
            -1000f,
            0f,
            0f,
            false,
            false);
        var input = new PlayerMovementInput(
            1,
            1,
            0f,
            0f,
            0f,
            PlayerMovementButtons.None);

        var result = PlayerMovementSimulation.Step(
            airborne,
            input,
            Settings,
            CollisionWorld);

        Assert.Equal(-Settings.MaximumFallSpeed, result.VelocityY);
    }

    [Fact]
    public void SettingsRejectMovementThatExceedsTheCollisionSubstepBudget()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MovementSimulationSettings(
            1,
            5f,
            8f,
            720f,
            -24f,
            1000f,
            7f,
            -2f,
            0f,
            -14f,
            14f,
            -14f,
            14f,
            CharacterCollisionSettings.Default));

        Assert.Contains("collision substep budget", exception.Message);
    }

    [Fact]
    public void SequenceComparisonHandlesUIntWrapAround()
    {
        Assert.True(MovementSequence.IsNewer(1u, uint.MaxValue));
        Assert.False(MovementSequence.IsNewer(uint.MaxValue, 1u));
        Assert.False(MovementSequence.IsNewer(42u, 42u));
    }

    private static PlayerMovementState CreateInitialState(float x, float y, float z)
    {
        return PlayerMovementSimulation.CreateInitialState(
            Settings,
            CollisionWorld,
            x,
            y,
            z,
            0f);
    }
}
