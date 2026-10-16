using System.Text.Json;
using Mmorpg.GameSimulation;
using Mmorpg.Testing;
using Mmorpg.WorldData.Content;

namespace Mmorpg.Shared.Tests;

public sealed class MovementPathPlannerTests
{
    private static readonly MovementSimulationSettings Settings = new(
        30,
        5f,
        720f,
        -24f,
        -2f,
        0f,
        -300f,
        300f,
        -300f,
        300f);

    private static readonly Lazy<ChunkedStaticCollisionWorld> DevelopmentWorld2 = new(LoadDevelopmentWorld2);

    [Fact]
    public void OpenGroundUsesOneStraightWaypointWithoutSearching()
    {
        var world = CompileWorld(Ground());

        var plan = Plan(world, new(0f, 0f, 0f), new(12f, 0f, -7f));

        Assert.Equal(MovementPathPlanStatus.Reached, plan.Status);
        Assert.Equal(0, plan.ExpandedNodes);
        var waypoint = Assert.Single(plan.Waypoints);
        Assert.Equal(new SimulationVector3(12f, 0f, -7f), waypoint);
        Assert.False(plan.IsTruncated);
    }

    [Fact]
    public void PlansAroundAWallAndTheMotorFollowsWithoutBeingBlocked()
    {
        var world = CompileWorld(Ground(), Box("Wall", 0f, 1.5f, 3f, 4f, 1.5f, 0.25f));
        var start = new SimulationVector3(0f, 0f, 0f);
        var destination = new SimulationVector3(0.5f, 0f, 6f);

        var plan = Plan(world, start, destination);

        Assert.Equal(MovementPathPlanStatus.Reached, plan.Status);
        Assert.True(plan.Waypoints.Count >= 2);
        Assert.Equal(destination, plan.Waypoints[^1]);
        AssertFollowsToTheEnd(world, Settings, start, plan);
    }

    [Fact]
    public void DestinationOnAWallFaceProjectsToTheNearestFreeGround()
    {
        var world = CompileWorld(Ground(), Box("Tower", 0f, 4f, 10f, 2f, 4f, 2f));

        // A click on the south face of the tower, two meters above the ground.
        var plan = Plan(world, new(0f, 0f, 0f), new(0.5f, 2f, 8f));

        Assert.Equal(MovementPathPlanStatus.Closest, plan.Status);
        var end = plan.Waypoints[^1];
        Assert.Equal(0f, end.Y);
        Assert.InRange(end.Z, 7f, 7.6f);
        Assert.InRange(end.X, -0.5f, 1.5f);
    }

    [Fact]
    public void UnreachableRoofEndsAtTheClosestReachablePointWithinTheBudget()
    {
        var world = CompileWorld(Ground(), Box("Tower", 0f, 4f, 10f, 2f, 4f, 2f));
        var start = new SimulationVector3(0f, 0f, 0f);

        var plan = Plan(world, start, new(0f, 8f, 10f));

        Assert.Equal(MovementPathPlanStatus.Closest, plan.Status);
        Assert.InRange(plan.ExpandedNodes, 1, MovementPathPlannerSettings.Default.MaximumExpandedNodes);
        var end = plan.Waypoints[^1];
        Assert.Equal(0f, end.Y);
        // The nearest free cell center keeps the clearance margin to the tower face at z = 8.
        Assert.InRange(Math.Abs(end.X), 0f, 2.5f);
        Assert.InRange(end.Z, 7f, 7.6f);
        AssertFollowsToTheEnd(world, Settings, start, plan);
    }

    [Fact]
    public void StandingAtTheDestinationOrInTheAirGivesNoPath()
    {
        var world = CompileWorld(Ground());

        Assert.Equal(
            MovementPathPlanStatus.NoPath,
            Plan(world, new(1f, 0f, 1f), new(1.005f, 0f, 1f)).Status);
        Assert.Equal(
            MovementPathPlanStatus.NoPath,
            Plan(world, new(1f, 20f, 1f), new(5f, 0f, 5f)).Status);
        Assert.Equal(
            MovementPathPlanStatus.NoPath,
            Plan(world, new(1f, 0f, 1f), new(float.NaN, 0f, 5f)).Status);
    }

    [Fact]
    public void DestinationsBeyondTheSearchAreaMoveTowardTheStart()
    {
        var world = CompileWorld(Ground());

        var plan = Plan(world, new(0f, 0f, 0f), new(200f, 0f, 0f));

        Assert.Equal(MovementPathPlanStatus.Closest, plan.Status);
        var end = Assert.Single(plan.Waypoints);
        Assert.InRange(end.X, 60f, 64f);
        Assert.Equal(0f, end.Z);
    }

    [Fact]
    public void DevelopmentWorld2RoutesAroundTheEastServiceHall()
    {
        var world = DevelopmentWorld2.Value;
        var settings = DevelopmentWorld2Settings();
        var start = new SimulationVector3(16f, 0f, 12f);
        var destination = new SimulationVector3(40f, 0f, 12f);

        var plan = new MovementPathPlanner(world, settings, MovementPathPlannerSettings.Default)
            .Plan(start, destination);

        Assert.Equal(MovementPathPlanStatus.Reached, plan.Status);
        Assert.False(plan.IsTruncated);
        Assert.True(plan.Waypoints.Count >= 3);
        AssertFollowsToTheEnd(world, settings, start, plan);
    }

    [Fact]
    public void DevelopmentWorld2HallRoofEndsBesideTheHallWithinTheBudget()
    {
        var world = DevelopmentWorld2.Value;
        var settings = DevelopmentWorld2Settings();
        var start = new SimulationVector3(0f, 0f, -16f);

        var plan = new MovementPathPlanner(world, settings, MovementPathPlannerSettings.Default)
            .Plan(start, new SimulationVector3(27f, 8f, 12f));

        Assert.Equal(MovementPathPlanStatus.Closest, plan.Status);
        Assert.Equal(MovementPathPlannerSettings.Default.MaximumExpandedNodes, plan.ExpandedNodes);
        var final = AssertFollowsToTheEnd(world, settings, start, plan);
        Assert.Equal(0f, final.PositionY, 3);
        Assert.InRange(final.PositionX, 21f, 22f);
    }

    [Fact]
    public void PlannerSettingsRejectInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementPathPlannerSettings(0.05f, 64f, 100, 0.05f, 100f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementPathPlannerSettings(0.5f, 300f, 100, 0.05f, 100f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementPathPlannerSettings(0.5f, 64f, 0, 0.05f, 100f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementPathPlannerSettings(0.5f, 64f, 100, -0.1f, 100f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementPathPlannerSettings(0.5f, 64f, 100, 0.05f, 300f));
        Assert.True(MovementPathPlannerSettings.Default.MaximumPathLength < MovementPathRules.MaximumLength);
    }

    [Fact]
    public void TrimToRangeStopsWhereTheRouteFirstEntersTheRange()
    {
        var start = new SimulationVector3(0f, 0f, 0f);
        var route = new[]
        {
            new SimulationVector3(10f, 0f, 0f),
            new SimulationVector3(10f, 1f, 10f)
        };

        var trimmed = MovementPathGeometry.TrimToRange(start, route, new SimulationVector3(10f, 0f, 10f), 2.5f);

        Assert.Equal(2, trimmed.Length);
        Assert.Equal(route[0], trimmed[0]);
        Assert.Equal(10f, trimmed[1].X, 4);
        Assert.Equal(7.5f, trimmed[1].Z, 4);
        Assert.Equal(0.75f, trimmed[1].Y, 4);

        // Already inside the range: nothing to walk.
        Assert.Empty(MovementPathGeometry.TrimToRange(start, route, new SimulationVector3(1f, 0f, 1f), 2f));

        // A route that never comes within range stays whole.
        Assert.Equal(route, MovementPathGeometry.TrimToRange(start, route, new SimulationVector3(-50f, 0f, 0f), 2f));
        Assert.Throws<ArgumentOutOfRangeException>(() => MovementPathGeometry.TrimToRange(start, route, start, -1f));
    }

    [Fact]
    public void CollisionRaycastFindsTheNearestEnteredBox()
    {
        var world = CompileWorld(Ground(), Box("Wall", 0f, 1.5f, 3f, 4f, 1.5f, 0.25f));
        var buffer = new CollisionQueryBuffer();

        Assert.True(CollisionRaycast.TryRaycast(
            world,
            new SimulationVector3(0f, 1f, -5f),
            new SimulationVector3(0f, 0f, 1f),
            50f,
            CollisionLayers.CharacterMovement,
            buffer,
            out var wallHit));
        Assert.Equal(7.75f, wallHit.Distance, 4);
        Assert.Equal(-1f, wallHit.Normal.Z, 4);

        Assert.True(CollisionRaycast.TryRaycast(
            world,
            new SimulationVector3(5f, 10f, 5f),
            new SimulationVector3(0f, -1f, 0f),
            50f,
            CollisionLayers.CharacterMovement,
            buffer,
            out var groundHit));
        Assert.Equal(0f, groundHit.Point.Y, 4);
        Assert.False(CollisionRaycast.TryRaycast(
            world,
            new SimulationVector3(5f, 10f, 5f),
            new SimulationVector3(0f, 1f, 0f),
            50f,
            CollisionLayers.CharacterMovement,
            buffer,
            out _));
    }

    private static MovementPathPlan Plan(
        ICollisionWorld world,
        SimulationVector3 start,
        SimulationVector3 destination)
    {
        return new MovementPathPlanner(world, Settings, MovementPathPlannerSettings.Default)
            .Plan(start, destination);
    }

    private static PlayerMovementState AssertFollowsToTheEnd(
        ICollisionWorld world,
        MovementSimulationSettings settings,
        SimulationVector3 start,
        MovementPathPlan plan)
    {
        var path = new MovementPath(1, plan.Waypoints.ToArray());
        Assert.True(MovementPathRules.IsValid(path, start, settings, out var error), error);
        var state = PlayerMovementSimulation.CreateInitialState(settings, world, start.X, start.Y, start.Z, 0f);
        var maximumBlockedTicks = 0;
        for (uint tick = 1; tick <= 3_000 && state.PathProgress.NextWaypointIndex < path.Count; tick++)
        {
            state = PlayerMovementSimulation.Step(
                state,
                new PlayerMovementInput(tick, tick, path),
                settings,
                PlayerCarryState.Default,
                world);
            maximumBlockedTicks = Math.Max(maximumBlockedTicks, state.PathProgress.BlockedTicks);
        }

        Assert.Equal(path.Count, state.PathProgress.NextWaypointIndex);
        Assert.True(maximumBlockedTicks <= 1, $"The path was blocked for {maximumBlockedTicks} ticks.");
        var deltaX = state.PositionX - path.Destination.X;
        var deltaZ = state.PositionZ - path.Destination.Z;
        Assert.True(
            (deltaX * deltaX) + (deltaZ * deltaZ) < 0.0004f,
            $"Ended at ({state.PositionX}, {state.PositionY}, {state.PositionZ}).");
        return state;
    }

    private static MovementSimulationSettings DevelopmentWorld2Settings()
    {
        return new MovementSimulationSettings(
            30,
            5f,
            720f,
            -24f,
            -2f,
            0f,
            -254f,
            254f,
            -254f,
            254f,
            CharacterCollisionSettings.Default);
    }

    private static ChunkedStaticCollisionWorld LoadDevelopmentWorld2()
    {
        var authoring = JsonSerializer.Deserialize<CollisionWorldAuthoringDocument>(
            File.ReadAllText(ContentLayout.CollisionSourcePath(RepositoryPaths.Content, "development-world-2")),
            new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true })!;
        return ToWorld(CollisionWorldCompiler.Compile(authoring));
    }

    private static ChunkedStaticCollisionWorld CompileWorld(params CollisionBoxAuthoringEntry[] boxes)
    {
        return ToWorld(CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "planner-tests",
            ChunkSize = 32f,
            Boxes = boxes
        }));
    }

    private static ChunkedStaticCollisionWorld ToWorld(CollisionWorldBakeResult bake)
    {
        return new ChunkedStaticCollisionWorld(
            bake.Manifest.WorldId,
            bake.Manifest.Revision,
            bake.Manifest.ChunkSize,
            bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
    }

    private static CollisionBoxAuthoringEntry Ground()
    {
        return Box("Ground", 0f, -0.25f, 0f, 300f, 0.25f, 300f);
    }

    private static CollisionBoxAuthoringEntry Box(
        string name,
        float centerX,
        float centerY,
        float centerZ,
        float halfExtentX,
        float halfExtentY,
        float halfExtentZ)
    {
        return new CollisionBoxAuthoringEntry
        {
            SourcePath = "Planner/" + name + "#BoxCollider0",
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
