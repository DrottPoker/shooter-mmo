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
    private static readonly PlayerCarryState Unencumbered = PlayerCarryState.Default;

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

        var result = PlayerMovementSimulation.Step(
            initial,
            input,
            Settings,
            Unencumbered,
            CollisionWorld);

        Assert.True(result.PositionX > 0f);
        Assert.InRange(Math.Abs(result.PositionZ), 0f, 0.0001f);
        Assert.InRange(result.YawDegrees, 23.999f, 24.001f);
    }

    [Fact]
    public void AirborneInputCannotStartMovementOrSprint()
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

        var result = PlayerMovementSimulation.Step(
            airborne,
            input,
            Settings,
            Unencumbered,
            CollisionWorld);

        Assert.False(result.IsSprinting);
        Assert.Equal(0f, result.VelocityX);
        Assert.Equal(0f, result.VelocityZ);
        Assert.Equal(0f, result.YawDegrees);
    }

    [Fact]
    public void AirborneInputCannotRedirectExistingMomentumOrFacing()
    {
        var airborne = new PlayerMovementState(
            0f,
            2f,
            0f,
            2f,
            1f,
            6f,
            35f,
            false,
            true);
        var input = new PlayerMovementInput(
            1,
            1,
            -1f,
            -1f,
            180f,
            PlayerMovementButtons.Aim);

        var result = PlayerMovementSimulation.Step(
            airborne,
            input,
            Settings,
            Unencumbered,
            CollisionWorld);

        Assert.False(result.IsSprinting);
        Assert.Equal(2f, result.VelocityX);
        Assert.Equal(6f, result.VelocityZ);
        Assert.Equal(35f, result.YawDegrees);
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

        var sprinting = PlayerMovementSimulation.Step(
            grounded,
            sprint,
            Settings,
            Unencumbered,
            CollisionWorld);
        var airborne = PlayerMovementSimulation.Step(
            sprinting,
            jump,
            Settings,
            Unencumbered,
            CollisionWorld);

        Assert.True(airborne.IsSprinting);
        Assert.False(airborne.IsGrounded);
        Assert.Equal(Settings.SprintSpeed, airborne.VelocityZ);
    }

    [Fact]
    public void AimCancelsSprintAndUsesWalkSpeed()
    {
        var grounded = CreateInitialState(0f, 0f, 0f);
        var sprinting = PlayerMovementSimulation.Step(
            grounded,
            new PlayerMovementInput(
                1,
                1,
                0f,
                1f,
                0f,
                PlayerMovementButtons.Sprint),
            Settings,
            Unencumbered,
            CollisionWorld);

        var aiming = PlayerMovementSimulation.Step(
            sprinting,
            new PlayerMovementInput(
                2,
                2,
                0f,
                1f,
                0f,
                PlayerMovementButtons.Sprint | PlayerMovementButtons.Aim),
            Settings,
            Unencumbered,
            CollisionWorld);

        Assert.False(aiming.IsSprinting);
        Assert.Equal(Settings.WalkSpeed, aiming.VelocityZ);
    }

    [Fact]
    public void AimBlocksJumpEvenWhenJumpFlagIsSet()
    {
        var grounded = CreateInitialState(0f, 0f, 0f);
        var aimingAndJumping = PlayerMovementSimulation.Step(
            grounded,
            new PlayerMovementInput(
                1,
                1,
                0f,
                0f,
                0f,
                PlayerMovementButtons.Aim | PlayerMovementButtons.Jump),
            Settings,
            Unencumbered,
            CollisionWorld);

        Assert.True(aimingAndJumping.IsGrounded);
        Assert.Equal(Settings.GroundedVerticalVelocity, aimingAndJumping.VelocityY);
        Assert.Equal(grounded.PositionY, aimingAndJumping.PositionY);
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

        var result = PlayerMovementSimulation.Step(
            initial,
            input,
            Settings,
            Unencumbered,
            CollisionWorld);

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
            Unencumbered,
            CollisionWorld);

        Assert.Equal(-Settings.MaximumFallSpeed, result.VelocityY);
    }

    [Theory]
    [InlineData(200, 10000)]
    [InlineData(210, 9000)]
    [InlineData(220, 8000)]
    [InlineData(240, 6000)]
    [InlineData(260, 4000)]
    [InlineData(280, 2000)]
    public void SharedEncumbranceCurveUsesExactFixedPointReferenceValues(
        long carriedWeight,
        int expectedBasisPoints)
    {
        var carryState = new PlayerCarryState(1, carriedWeight, 200);

        Assert.Equal(expectedBasisPoints, carryState.MovementMultiplierBasisPoints);
    }

    [Fact]
    public void SprintIsAllowedAtCapacityAndBlockedAboveCapacity()
    {
        var grounded = CreateInitialState(0f, 0f, 0f);
        var sprintInput = new PlayerMovementInput(
            1,
            1,
            0f,
            1f,
            0f,
            PlayerMovementButtons.Sprint);

        var atCapacity = PlayerMovementSimulation.Step(
            grounded,
            sprintInput,
            Settings,
            new PlayerCarryState(1, 200, 200),
            CollisionWorld);
        var aboveCapacity = PlayerMovementSimulation.Step(
            grounded,
            sprintInput,
            Settings,
            new PlayerCarryState(2, 210, 200),
            CollisionWorld);

        Assert.True(atCapacity.IsSprinting);
        Assert.Equal(Settings.SprintSpeed, atCapacity.VelocityZ);
        Assert.False(aboveCapacity.IsSprinting);
        Assert.Equal(Settings.WalkSpeed * 0.9f, aboveCapacity.VelocityZ, 4);
    }

    [Fact]
    public void HardCapUsesTwentyPercentOfBaseMovementAndRejectsHigherState()
    {
        var grounded = CreateInitialState(0f, 0f, 0f);
        var input = new PlayerMovementInput(
            1,
            1,
            0f,
            1f,
            0f,
            PlayerMovementButtons.None);
        var atHardCap = new PlayerCarryState(1, 280, 200);

        var result = PlayerMovementSimulation.Step(
            grounded,
            input,
            Settings,
            atHardCap,
            CollisionWorld);

        Assert.Equal(Settings.WalkSpeed * 0.2f, result.VelocityZ, 4);
        Assert.Throws<ArgumentException>(() => new PlayerCarryState(2, 281, 200));
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
