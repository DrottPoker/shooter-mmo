using Mmorpg.GameSimulation;
using Mmorpg.Testing.Content;

namespace Mmorpg.Shared.Tests;

public sealed class PlayerMovementSimulationTests
{
    private static readonly ChunkedStaticCollisionWorld CollisionWorld =
        CollisionTestWorldFactory.Create();
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
    private static readonly PlayerCarryState Unencumbered = PlayerCarryState.Default;

    [Fact]
    public void PathStepMovesTowardTheNextWaypointAndTurnsTowardIt()
    {
        var initial = CreateInitialState(0f, 0f, 0f);
        var path = Path(1, (10f, 0f));

        var result = Step(initial, 1, path);

        Assert.InRange(result.PositionX, Settings.WalkSpeed / 30f - 0.0001f, Settings.WalkSpeed / 30f + 0.0001f);
        Assert.InRange(Math.Abs(result.PositionZ), 0f, 0.0001f);
        Assert.InRange(result.YawDegrees, 23.999f, 24.001f);
        Assert.Equal(new MovementPathProgress(1, 0, 0), result.PathProgress);
    }

    [Fact]
    public void PathLandsExactlyOnEveryWaypointAndStandsAtTheDestination()
    {
        var state = CreateInitialState(0f, 0f, 0f);
        var path = Path(7, (1f, 0f), (1f, 1.25f));

        var ticks = 0;
        while (state.PathProgress.NextWaypointIndex < path.Count && ticks < 30)
        {
            ticks++;
            state = Step(state, (uint)ticks, path);
        }

        // One meter takes 6 full ticks, and 1.25 meters take 7 full ticks and a partial one.
        Assert.Equal(14, ticks);
        Assert.Equal(new MovementPathProgress(7, 2, 0), state.PathProgress);
        Assert.InRange(Math.Abs(state.PositionX - 1f), 0f, 0.0001f);
        Assert.InRange(Math.Abs(state.PositionZ - 1.25f), 0f, 0.0001f);

        var standing = Step(state, (uint)(ticks + 1), path);
        Assert.Equal(state.PositionX, standing.PositionX);
        Assert.Equal(state.PositionZ, standing.PositionZ);
        Assert.Equal(0f, standing.VelocityX);
        Assert.Equal(0f, standing.VelocityZ);
    }

    [Fact]
    public void ANewPathRestartsProgressAndNoPathStandsStill()
    {
        var state = CreateInitialState(0f, 0f, 0f);
        var first = Path(1, (0f, 5f), (5f, 5f));
        for (uint tick = 1; tick <= 3; tick++)
        {
            state = Step(state, tick, first);
        }

        var second = Path(2, (-5f, 0f));
        var redirected = Step(state, 4, second);
        Assert.Equal(new MovementPathProgress(2, 0, 0), redirected.PathProgress);
        Assert.True(redirected.PositionX < state.PositionX);

        var stopped = Step(redirected, 5, null);
        Assert.Equal(MovementPathProgress.None, stopped.PathProgress);
        Assert.Equal(redirected.PositionX, stopped.PositionX);
        Assert.Equal(redirected.PositionZ, stopped.PositionZ);
    }

    [Fact]
    public void AirborneCharacterKeepsMomentumAndFacingWhileItsPathWaits()
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
            new MovementPathProgress(3, 1, 4));
        var path = Path(3, (-5f, -5f), (-6f, -6f));

        var result = Step(airborne, 1, path);

        Assert.Equal(2f, result.VelocityX);
        Assert.Equal(6f, result.VelocityZ);
        Assert.Equal(35f, result.YawDegrees);
        Assert.Equal(new MovementPathProgress(3, 1, 4), result.PathProgress);
    }

    [Fact]
    public void WorldBoundsClampAuthoritativePosition()
    {
        var initial = CreateInitialState(13.95f, 0f, 0f);

        var result = Step(initial, 1, Path(1, (20f, 0f)));

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
            false);

        var result = Step(airborne, 1, null);

        Assert.Equal(-Settings.MaximumFallSpeed, result.VelocityY);
    }

    [Fact]
    public void BlockedPathEndsAfterHalfASecondWithoutDrift()
    {
        var world = CompileWorld(
            Box("Blocked/Ground", 0f, -0.25f, 0f, 15f, 0.25f, 15f),
            Box("Blocked/Wall", 0f, 1.5f, 2f, 4f, 1.5f, 0.25f));
        var state = PlayerMovementSimulation.CreateInitialState(Settings, world, 0f, 0f, 0f, 0f);
        var path = Path(4, (0f, 6f));

        var ticks = 0;
        while (state.PathProgress.NextWaypointIndex < path.Count && ticks < 60)
        {
            ticks++;
            state = PlayerMovementSimulation.Step(
                state,
                new PlayerMovementInput((uint)ticks, (uint)ticks, path),
                Settings,
                Unencumbered,
                world);
        }

        // The capsule reaches the wall after 9 ticks and gives up 15 ticks later.
        Assert.Equal(9 + Settings.BlockedPathTickLimit, ticks);
        Assert.Equal(new MovementPathProgress(4, 1, 0), state.PathProgress);
        Assert.InRange(state.PositionZ, 1.39f, 1.401f);
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
    public void CarryMultiplierScalesPathSpeedDownToTheHardCapMinimum()
    {
        var grounded = CreateInitialState(0f, 0f, 0f);
        var path = Path(1, (0f, 10f));

        var aboveCapacity = PlayerMovementSimulation.Step(
            grounded,
            new PlayerMovementInput(1, 1, path),
            Settings,
            new PlayerCarryState(1, 210, 200),
            CollisionWorld);
        var atHardCap = PlayerMovementSimulation.Step(
            grounded,
            new PlayerMovementInput(1, 1, path),
            Settings,
            new PlayerCarryState(1, 280, 200),
            CollisionWorld);

        // Death can leave retained weight above the hard cap; it moves at the minimum.
        var overflow = PlayerMovementSimulation.Step(
            grounded,
            new PlayerMovementInput(1, 1, path),
            Settings,
            new PlayerCarryState(2, 348, 200),
            CollisionWorld);

        Assert.Equal(Settings.WalkSpeed * 0.9f, aboveCapacity.VelocityZ, 4);
        Assert.Equal(Settings.WalkSpeed * 0.2f, atHardCap.VelocityZ, 4);
        Assert.Equal(Settings.WalkSpeed * 0.2f, overflow.VelocityZ, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerCarryState(3, -1, 200));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerCarryState(3, 10, 0));
    }

    [Fact]
    public void SettingsRejectMovementThatExceedsTheCollisionSubstepBudget()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MovementSimulationSettings(
            1,
            5f,
            720f,
            -24f,
            1000f,
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
    public void MovementPathRejectsInvalidShapes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Path(0, (1f, 1f)));
        Assert.Throws<ArgumentException>(() => new MovementPath(1, Array.Empty<SimulationVector3>()));
        Assert.Throws<ArgumentException>(() => new MovementPath(
            1,
            Enumerable.Range(0, MovementPath.MaximumWaypoints + 1)
                .Select(index => new SimulationVector3(index, 0f, 0f))
                .ToArray()));
        Assert.Throws<ArgumentException>(() => new MovementPath(
            1,
            [new SimulationVector3(float.NaN, 0f, 0f)]));
        Assert.Throws<ArgumentNullException>(() => new MovementPath(1, null!));
    }

    [Fact]
    public void MovementPathProgressRejectsImpossibleValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementPathProgress(1, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementPathProgress(1, MovementPath.MaximumWaypoints + 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementPathProgress(1, 0, 256));
        Assert.Throws<ArgumentException>(() => new MovementPathProgress(0, 1, 0));
        Assert.True(new MovementPathProgress(5, 0, 0).IsFollowing(Path(5, (1f, 0f))));
        Assert.False(new MovementPathProgress(5, 1, 0).IsFollowing(Path(5, (1f, 0f))));
        Assert.False(new MovementPathProgress(5, 0, 0).IsFollowing(Path(6, (1f, 0f))));
    }

    [Fact]
    public void MovementPathRulesKeepWaypointsInsideTheWorldAndBoundTheLength()
    {
        var wide = new MovementSimulationSettings(30, 5f, 720f, -24f, -2f, 0f, -500f, 500f, -500f, 500f);
        var start = new SimulationVector3(0f, 0f, 0f);

        Assert.True(MovementPathRules.IsValid(Path(1, (100f, 0f), (100f, 156f)), start, wide, out _));
        Assert.False(MovementPathRules.IsValid(Path(1, (100f, 0f), (100f, 157f)), start, wide, out var tooLong));
        Assert.Contains("longer", tooLong, StringComparison.Ordinal);
        Assert.False(MovementPathRules.IsValid(Path(1, (14.5f, 0f)), start, Settings, out var outside));
        Assert.Contains("outside", outside, StringComparison.Ordinal);
        Assert.False(MovementPathRules.IsValid(
            new MovementPath(1, [new SimulationVector3(1f, 2_000_000f, 1f)]),
            start,
            Settings,
            out _));
    }

    [Fact]
    public void SequenceComparisonHandlesUIntWrapAround()
    {
        Assert.True(MovementSequence.IsNewer(1u, uint.MaxValue));
        Assert.False(MovementSequence.IsNewer(uint.MaxValue, 1u));
        Assert.False(MovementSequence.IsNewer(42u, 42u));
    }

    private static PlayerMovementState Step(PlayerMovementState state, uint tick, MovementPath? path)
    {
        return PlayerMovementSimulation.Step(
            state,
            new PlayerMovementInput(tick, tick, path),
            Settings,
            Unencumbered,
            CollisionWorld);
    }

    private static MovementPath Path(uint id, params (float X, float Z)[] waypoints)
    {
        return new MovementPath(
            id,
            waypoints.Select(waypoint => new SimulationVector3(waypoint.X, 0f, waypoint.Z)).ToArray());
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

    private static ChunkedStaticCollisionWorld CompileWorld(params CollisionBoxAuthoringEntry[] boxes)
    {
        var bake = CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "movement-tests",
            ChunkSize = 32f,
            Boxes = boxes
        });
        return new ChunkedStaticCollisionWorld(
            bake.Manifest.WorldId,
            bake.Manifest.Revision,
            bake.Manifest.ChunkSize,
            bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
    }

    private static CollisionBoxAuthoringEntry Box(
        string sourcePath,
        float centerX,
        float centerY,
        float centerZ,
        float halfExtentX,
        float halfExtentY,
        float halfExtentZ)
    {
        return new CollisionBoxAuthoringEntry
        {
            SourcePath = sourcePath,
            LayerMask = CollisionLayers.CharacterMovement,
            CenterX = centerX,
            CenterY = centerY,
            CenterZ = centerZ,
            HalfExtentX = halfExtentX,
            HalfExtentY = halfExtentY,
            HalfExtentZ = halfExtentZ,
            RotationW = 1f
        };
    }
}
