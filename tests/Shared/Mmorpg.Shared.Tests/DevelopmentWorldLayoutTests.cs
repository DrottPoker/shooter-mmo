using System.Text.Json;
using Mmorpg.GameSimulation;
using Mmorpg.Testing;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Worlds;
using static Mmorpg.Shared.Tests.MovementRouteAssertions;

namespace Mmorpg.Shared.Tests;

// Content tests of the baked development Worlds: every test station behaves as its label
// says when walked with the shared route planner and movement simulation, every actor
// stands on clear ground, and the distances that the stress arena and the interest road
// rely on hold against the SimulationWorker configuration. The station coordinates match
// the Unity builders in src/Client/Assets/Editor/DevelopmentWorlds.
public sealed class DevelopmentWorldLayoutTests
{
    private const string StressArena = "development-world-1";
    private const string TestWorld = "development-world-2";

    private static readonly Lazy<ChunkedStaticCollisionWorld> DevelopmentWorld1 =
        new(() => LoadCheckedInWorld(StressArena));

    private static readonly Lazy<ChunkedStaticCollisionWorld> DevelopmentWorld2 =
        new(() => LoadCheckedInWorld(TestWorld));

    private static readonly Lazy<JsonElement> WorkerSettings = new(LoadWorkerSettings);

    public static TheoryData<string> CheckedInWorldIds => new(WorldActorTestData.CheckedInWorldIds());

    [Theory]
    [MemberData(nameof(CheckedInWorldIds))]
    public void EveryActorAndPatrolPointStandsOnClearGroundInsideTheBounds(string worldId)
    {
        var manifest = LoadManifest(worldId);
        var world = LoadCheckedInWorld(worldId);
        var runtime = WorldActorTestData.CompileWorld(worldId);
        var points = runtime.SpawnInstances
            .Select(instance => (Name: instance.Id, Position: new SimulationVector3(instance.X, instance.Y, instance.Z)))
            .Concat(runtime.PatrolPaths.SelectMany(path => path.Points.Select((point, index) =>
                (Name: path.Id + "#" + index, Position: new SimulationVector3(point.X, point.Y, point.Z)))))
            .Append((Name: "spawn", Position: new SimulationVector3(manifest.spawn.x, manifest.spawn.y, manifest.spawn.z)));

        foreach (var (name, position) in points)
        {
            Assert.InRange(position.X, manifest.bounds.minimumX + 1f, manifest.bounds.maximumX - 1f);
            Assert.InRange(position.Z, manifest.bounds.minimumZ + 1f, manifest.bounds.maximumZ - 1f);
            var buffer = new CollisionQueryBuffer();
            Assert.True(
                CollisionRaycast.TryRaycast(
                    world,
                    new SimulationVector3(position.X, position.Y + 3f, position.Z),
                    new SimulationVector3(0f, -1f, 0f),
                    6f,
                    CollisionLayers.CharacterMovement,
                    buffer,
                    out var hit),
                name + " has no ground below it.");
            Assert.True(MathF.Abs(hit.Point.Y - position.Y) < 0.05f, $"{name} stands at {position.Y}, the ground is at {hit.Point.Y}.");

            // A 0.6 m radius column above the step height must be free for any actor body.
            var column = new CollisionQueryBuffer();
            world.QueryShapes(
                new CollisionAabb(
                    new SimulationVector3(position.X - 0.6f, position.Y + 0.4f, position.Z - 0.6f),
                    new SimulationVector3(position.X + 0.6f, position.Y + 1.8f, position.Z + 0.6f)),
                CollisionLayers.CharacterMovement,
                column);
            Assert.True(column.Shapes.Count == 0, $"{name} at ({position.X}, {position.Z}) overlaps {column.Shapes.Count} collision shapes.");
        }
    }

    [Fact]
    public void StressArenaKeepsEveryPointInsideOneInterestRadius()
    {
        var bounds = LoadManifest(StressArena).bounds;
        var diagonal = MathF.Sqrt(
            MathF.Pow(bounds.maximumX - bounds.minimumX, 2f)
            + MathF.Pow(bounds.maximumZ - bounds.minimumZ, 2f));

        Assert.True(diagonal < InterestEnterRadius(), $"The arena diagonal of {diagonal} m leaves the interest radius.");
    }

    // StackStressGenerator walks a square of two seconds of walking per side from the spawn
    // without route planning, so its straight legs must be free.
    [Fact]
    public void StressBotSquareFromTheSpawnIsFree()
    {
        var spawn = LoadManifest(StressArena).spawn;
        var leg = WalkSpeed() * 2f;
        var settings = SettingsFor(StressArena);
        var start = new SimulationVector3(spawn.x, spawn.y, spawn.z);
        var corners = new[]
        {
            new SimulationVector3(spawn.x, spawn.y, spawn.z + leg),
            new SimulationVector3(spawn.x + leg, spawn.y, spawn.z + leg),
            new SimulationVector3(spawn.x + leg, spawn.y, spawn.z),
            start
        };

        foreach (var corner in corners)
        {
            var final = AssertFollowsToTheEnd(DevelopmentWorld1.Value, settings, start, [corner]);
            start = new SimulationVector3(final.PositionX, final.PositionY, final.PositionZ);
        }
    }

    [Theory]
    [InlineData("Mira", 0f, 0f, 23f)]
    [InlineData("the Iron Veins", 18.5f, 0f, -2f)]
    [InlineData("inside the wolf pen", -24f, 0f, 0f)]
    [InlineData("the platform", 10f, 1f, -24f)]
    [InlineData("the foot of the platform stairs", 10f, 0f, -30f)]
    public void StressArenaHotspotsAreReachableFromTheSpawn(string target, float x, float y, float z)
    {
        var spawn = LoadManifest(StressArena).spawn;

        AssertReached(StressArena, DevelopmentWorld1.Value, target, new(spawn.x, spawn.y, spawn.z), new(x, y, z));
    }

    [Fact]
    public void TownRoutesAroundTheServiceHall()
    {
        var (plan, _) = AssertReached(TestWorld, DevelopmentWorld2.Value, "north of the hall", new(0f, 0f, 8f), new(0f, 0f, 26f));

        Assert.True(plan.Waypoints.Count >= 3);
    }

    [Fact]
    public void TownHallRoofEndsOnOpenGroundBesideTheHall()
    {
        var settings = SettingsFor(TestWorld);
        var start = new SimulationVector3(0f, 0f, -12f);

        var plan = Planner(DevelopmentWorld2.Value, settings).Plan(start, new SimulationVector3(0f, 6.5f, 16f));

        Assert.Equal(MovementPathPlanStatus.Closest, plan.Status);
        var final = AssertFollowsToTheEnd(DevelopmentWorld2.Value, settings, start, plan);
        Assert.Equal(0f, final.PositionY, 3);
        Assert.True(IsOpenAbove(DevelopmentWorld2.Value, final, 6.5f));
        Assert.InRange(DistanceXz(final, 0f, 16f), 4f, 7f);
    }

    [Theory]
    [InlineData("under the arcade roof", 22f, 10f, 22f, 22f)]
    [InlineData("into the house through its door", -22f, -10f, -22f, -22f)]
    public void TownRoofsCanBeWalkedUnder(string target, float startX, float startZ, float x, float z)
    {
        var (_, final) = AssertReached(TestWorld, DevelopmentWorld2.Value, target, new(startX, 0f, startZ), new(x, 0f, z));

        Assert.False(IsOpenAbove(DevelopmentWorld2.Value, final, 10f), $"No roof above {target}.");
    }

    [Theory]
    [InlineData(-20f, 0.15f, true)]
    [InlineData(-12f, 0.25f, true)]
    [InlineData(-4f, 0.34f, true)]
    [InlineData(4f, 0.36f, false)]
    [InlineData(12f, 0.5f, false)]
    public void StepPadsFollowTheStepHeightLimit(float x, float rise, bool passable)
    {
        AssertStation(new(x, 0f, 73f), new(x, rise, 78f), passable, "step " + rise);
    }

    [Theory]
    [InlineData(-20f, 20f, true)]
    [InlineData(-12f, 35f, true)]
    [InlineData(-4f, 42f, true)]
    [InlineData(4f, 48f, false)]
    [InlineData(12f, 60f, false)]
    public void SlopePadsFollowTheSlopeLimit(float x, float degrees, bool passable)
    {
        AssertStation(new(x, 0f, 86f), new(x, 2f, 98f), passable, "slope " + degrees);
    }

    [Theory]
    [InlineData(4.25f, 1.5f, true)]
    [InlineData(12.25f, 1f, true)]
    [InlineData(20.25f, 0.6f, false)]
    public void GapsFollowTheBodyWidth(float x, float width, bool passable)
    {
        var settings = SettingsFor(TestWorld);
        var start = new SimulationVector3(x, 0f, 109f);

        var plan = Planner(DevelopmentWorld2.Value, settings).Plan(start, new SimulationVector3(x, 0f, 116f));

        Assert.True(plan.Status == MovementPathPlanStatus.Reached, $"The {width} m gap plan ended as {plan.Status}.");
        AssertFollowsToTheEnd(DevelopmentWorld2.Value, settings, start, plan);
        var length = PathLength(start, plan);
        if (passable)
        {
            Assert.True(length < 9f, $"The route through the {width} m gap is {length} m long.");
        }
        else
        {
            Assert.True(length > 12f, $"The route went through the {width} m gap.");
        }
    }

    [Theory]
    [InlineData("the stairs platform", -32f, 0f, 104f, -32f, 3f, 116f)]
    [InlineData("the west platform by its ramp", -20f, 0f, 108f, -20f, 4f, 140f)]
    [InlineData("the east platform by its stairs", 20f, 0f, 122f, 20f, 4f, 140f)]
    [InlineData("across the bridge", -20f, 4f, 140f, 20f, 4f, 140f)]
    [InlineData("under the bridge", 0f, 0f, 133f, 0f, 0f, 147f)]
    [InlineData("through the maze", -55f, 0f, 79f, -55f, 0f, 91f)]
    [InlineData("through the low corridor", 44f, 0f, 78f, 44f, 0f, 94f)]
    [InlineData("through the narrow alley", 41f, 0f, 98f, 41f, 0f, 118f)]
    [InlineData("into the arena", 74f, 0f, 0f, 100f, 0f, 0f)]
    public void MovementCourseAndArenaAreReachableByWalking(
        string target,
        float startX,
        float startY,
        float startZ,
        float x,
        float y,
        float z)
    {
        var (plan, _) = AssertReached(TestWorld, DevelopmentWorld2.Value, target, new(startX, startY, startZ), new(x, y, z));

        if (target == "through the maze")
        {
            Assert.True(plan.Waypoints.Count >= 4, "The maze route did not wind through its lanes.");
        }
    }

    // Routes never step off a ledge higher than the ground snap distance, so the way down
    // from the 3 m platform is back down its stairs; only walking straight off falls.
    [Fact]
    public void StairsPlatformLedgeIsLeftByTheStairsAndFallsWhenWalkedOff()
    {
        var settings = SettingsFor(TestWorld);
        var top = new SimulationVector3(-32f, 3f, 118f);
        var below = new SimulationVector3(-32f, 0f, 123f);

        var (plan, _) = AssertReached(TestWorld, DevelopmentWorld2.Value, "the ground below the ledge", top, below);
        Assert.True(PathLength(top, plan) > 15f, "The route stepped off the ledge.");

        var fallen = AssertFollowsToTheEnd(DevelopmentWorld2.Value, settings, top, [below]);
        Assert.Equal(0f, fallen.PositionY, 2);
    }

    // The highlands terrain: a hill, the peak by its east face from the east and from the
    // southeast around the south cliff, through the tunnel below the peak, and down the
    // peak's west face onto the terrain above the tunnel.
    [Theory]
    [InlineData("hilltop", 1, 104f, 0f, 64f, 126f, 4.02f, 98f)]
    [InlineData("peak from the east", 1, 156f, 0f, 152f, 118f, 20.02f, 152f)]
    [InlineData("peak from the southeast", 2, 150f, 0f, 124f, 118f, 20.02f, 152f)]
    [InlineData("through the tunnel", 1, 110f, 0f, 128f, 110f, 0.02f, 176f)]
    [InlineData("above the tunnel", 1, 118f, 20.02f, 152f, 111f, 15.82f, 152f)]
    public void HighlandsAreReachableByWalking(
        string target,
        int minimumWaypoints,
        float startX,
        float startY,
        float startZ,
        float x,
        float y,
        float z)
    {
        var settings = SettingsFor(TestWorld);
        var start = new SimulationVector3(startX, startY, startZ);

        var plan = Planner(DevelopmentWorld2.Value, settings).Plan(start, new SimulationVector3(x, y, z));

        Assert.True(plan.Status == MovementPathPlanStatus.Reached, $"The {target} plan ended as {plan.Status}.");
        Assert.False(plan.IsTruncated);
        Assert.True(plan.Waypoints.Count >= minimumWaypoints, $"The {target} route did not bend around the cliff.");
        var final = AssertFollowsToTheEnd(DevelopmentWorld2.Value, settings, start, plan);

        // On a slope the capsule rests slightly above the surface it touches.
        Assert.InRange(final.PositionY, y - 0.05f, y + 0.15f);
    }

    // The terrain above the tunnel faces up, so only the tunnel walls keep a walker out of
    // the hollow space below the peak.
    [Fact]
    public void HighlandsTunnelWallsKeepTheInsideOfThePeakUnreachable()
    {
        var settings = SettingsFor(TestWorld);
        var start = new SimulationVector3(110f, 0f, 152f);

        var plan = Planner(DevelopmentWorld2.Value, settings).Plan(start, new SimulationVector3(120f, 0f, 152f));

        Assert.Equal(MovementPathPlanStatus.Closest, plan.Status);
        var final = AssertFollowsToTheEnd(DevelopmentWorld2.Value, settings, start, plan);
        Assert.InRange(final.PositionX, 110f, 113f - 0.35f);
        Assert.Equal(0f, final.PositionY, 3);
    }

    // A click on the peak above the tunnel from south of the peak is unreachable within the
    // search budget. The tunnel floor right below the click is the closest point, but the
    // walk ends on open ground at the foot of the peak, next to the click.
    [Fact]
    public void HighlandsClickAboveTheTunnelEndsAtTheFootOfThePeakInsteadOfInTheTunnel()
    {
        var settings = SettingsFor(TestWorld);
        var start = new SimulationVector3(110f, 0f, 128f);

        var plan = Planner(DevelopmentWorld2.Value, settings).Plan(start, new SimulationVector3(111f, 15.82f, 152f));

        Assert.Equal(MovementPathPlanStatus.Closest, plan.Status);
        var final = AssertFollowsToTheEnd(DevelopmentWorld2.Value, settings, start, plan);
        Assert.True(IsOpenAbove(DevelopmentWorld2.Value, final, 15.82f), $"Ended under cover at ({final.PositionX}, {final.PositionZ}).");
        Assert.True(final.PositionY < 0.5f, $"Ended above the ground at {final.PositionY}.");
        Assert.InRange(DistanceXz(final, 111f, 152f), 10f, 17f);
    }

    // Walking the west road to the outpost must take the town out of interest and bring the
    // outpost guard in, so both sides of the exit radius are covered.
    [Fact]
    public void InterestRoadOutpostLiesBeyondTheInterestExitRadiusFromTheTown()
    {
        var runtime = WorldActorTestData.CompileWorld(TestWorld);
        var outpost = runtime.SpawnInstances.Single(instance => instance.SpawnDefinitionId == "development_world_2.outpost_guard");
        var spawn = LoadManifest(TestWorld).spawn;
        var town = runtime.SpawnInstances
            .Where(instance => MathF.Abs(instance.X) <= 36f && MathF.Abs(instance.Z) <= 36f)
            .Select(instance => (instance.X, instance.Z))
            .Append((spawn.x, spawn.z))
            .ToArray();

        Assert.NotEmpty(town);
        foreach (var (x, z) in town)
        {
            var distance = MathF.Sqrt(MathF.Pow(outpost.X - x, 2f) + MathF.Pow(outpost.Z - z, 2f));
            Assert.True(distance > InterestExitRadius() + 16f, $"The outpost is only {distance} m from ({x}, {z}).");
        }
    }

    private static void AssertStation(SimulationVector3 start, SimulationVector3 destination, bool passable, string station)
    {
        var settings = SettingsFor(TestWorld);
        var plan = Planner(DevelopmentWorld2.Value, settings).Plan(start, destination);
        if (passable)
        {
            Assert.True(plan.Status == MovementPathPlanStatus.Reached, $"The {station} plan ended as {plan.Status}.");
            var final = AssertFollowsToTheEnd(DevelopmentWorld2.Value, settings, start, plan);
            Assert.InRange(final.PositionY, destination.Y - 0.05f, destination.Y + 0.15f);
            return;
        }

        Assert.NotEqual(MovementPathPlanStatus.Reached, plan.Status);
        if (plan.Waypoints.Count > 0)
        {
            var final = AssertFollowsToTheEnd(DevelopmentWorld2.Value, settings, start, plan);
            Assert.True(final.PositionY < 0.4f, $"The {station} station was climbed to {final.PositionY}.");
        }
    }

    private static (MovementPathPlan Plan, PlayerMovementState Final) AssertReached(
        string worldId,
        ICollisionWorld world,
        string target,
        SimulationVector3 start,
        SimulationVector3 destination)
    {
        var settings = SettingsFor(worldId);
        var plan = Planner(world, settings).Plan(start, destination);

        Assert.True(plan.Status == MovementPathPlanStatus.Reached, $"The route to {target} ended as {plan.Status}.");
        Assert.False(plan.IsTruncated);
        var final = AssertFollowsToTheEnd(world, settings, start, plan);
        Assert.InRange(final.PositionY, destination.Y - 0.05f, destination.Y + 0.15f);
        return (plan, final);
    }

    private static MovementPathPlanner Planner(ICollisionWorld world, MovementSimulationSettings settings)
    {
        return new MovementPathPlanner(world, settings, MovementPathPlannerSettings.Default);
    }

    private static MovementSimulationSettings SettingsFor(string worldId)
    {
        var bounds = LoadManifest(worldId).bounds;
        return new MovementSimulationSettings(
            30,
            WalkSpeed(),
            720f,
            -24f,
            -2f,
            0f,
            bounds.minimumX,
            bounds.maximumX,
            bounds.minimumZ,
            bounds.maximumZ,
            CharacterCollisionSettings.Default);
    }

    private static WorldManifestDocument LoadManifest(string worldId)
    {
        var path = Path.Combine(ContentLayout.WorldDirectory(RepositoryPaths.Content, worldId), WorldManifestFormat.FileName);
        var manifest = JsonSerializer.Deserialize<WorldManifestDocument>(
            File.ReadAllText(path),
            new JsonSerializerOptions { IncludeFields = true })!;
        WorldManifestValidator.Validate(manifest);
        return manifest;
    }

    private static float WalkSpeed()
    {
        return WorkerSettings.Value.GetProperty("Movement").GetProperty("WalkSpeed").GetSingle();
    }

    private static float InterestEnterRadius()
    {
        return WorkerSettings.Value.GetProperty("InterestManagement").GetProperty("EnterRadius").GetSingle();
    }

    private static float InterestExitRadius()
    {
        return WorkerSettings.Value.GetProperty("InterestManagement").GetProperty("ExitRadius").GetSingle();
    }

    private static JsonElement LoadWorkerSettings()
    {
        var path = Path.Combine(RepositoryPaths.Root, "src", "Server", "SimulationWorker", "Config", "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("SimulationWorker").Clone();
    }
}
