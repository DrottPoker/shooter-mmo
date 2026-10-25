using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Testing;
using Mmorpg.Tools.ActiveSimulationBots;
using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.Tests;

public sealed class ActiveSimulationBotNavigationTests
{
    private static readonly RealtimeMovementSettings Movement = new(
        30,
        15,
        5f,
        720f,
        -24f,
        55f,
        -2f,
        0f,
        -14f,
        14f,
        -14f,
        14f,
        0.35f,
        2f,
        0.35f,
        45f,
        0.4f,
        0.1f,
        6);

    [Fact]
    public void LoadsTheCompiledWorldCollisionThatTheWorkerUses()
    {
        using var navigation = ActiveSimulationBotNavigation.Load(
            "WorldData/Worlds",
            "development-world-2",
            1);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src",
            "Shared",
            "WorldData",
            "Generated",
            "WorldCollision",
            "development-world-2",
            "manifest.json")));
        Assert.Equal(
            manifest.RootElement.GetProperty("Revision").GetString(),
            navigation.CollisionWorld.Revision);
    }

    [Fact]
    public async Task PlansAWalkAroundAWallInsteadOfIntoIt()
    {
        using var navigation = ActiveSimulationBotNavigation.Create(WorldWithWall(), 1);

        var plan = await navigation.TryPlan(
            Movement,
            new SimulationVector3(0f, 0f, -8f),
            new SimulationVector3(0f, 0f, 8f))!;

        Assert.Equal(MovementPathPlanStatus.Reached, plan.Status);
        Assert.True(plan.Waypoints.Count > 1);
        Assert.Contains(plan.Waypoints, waypoint => Math.Abs(waypoint.X) > 4f);
    }

    [Fact]
    public async Task WalkingBotKeepsItsPathUntilThePlanArrivesThenFollowsIt()
    {
        using var navigation = ActiveSimulationBotNavigation.Create(WorldWithWall(), 1);
        var options = WalkingOptions();
        var source = new ActiveSimulationBotInputSource(options, 1, navigation);
        var joined = JoinedSession(navigation.CollisionWorld.Revision);
        var position = new SimulationBotPosition(0f, 0f, -8f);

        Assert.Null(source.SelectPath(1, joined, position));
        RealtimeMovementPath? path = null;
        for (var tick = 2u; tick < 200 && path is null; tick++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
            path = source.SelectPath(tick, joined, position);
        }

        Assert.NotNull(path);
        Assert.Equal(1, source.PlannedWalks);
        Assert.Equal(0, source.StraightWalks);
        Assert.InRange(path.Waypoints.Length, 1, MovementPath.MaximumWaypoints);
    }

    [Fact]
    public void WithoutMatchingCollisionTheBotWalksAStraightLine()
    {
        using var navigation = ActiveSimulationBotNavigation.Create(WorldWithWall(), 1);
        var options = WalkingOptions();
        var source = new ActiveSimulationBotInputSource(options, 1, navigation);

        var path = source.SelectPath(
            1,
            JoinedSession("another-collision-revision"),
            new SimulationBotPosition(0f, 0f, -8f));

        Assert.NotNull(path);
        Assert.Single(path.Waypoints);
        Assert.Equal(1, source.StraightWalks);
    }

    // Pinned, so a random source seeded per process, like System.HashCode, fails here.
    [Fact]
    public void TheSameSeedGivesABotTheSameWalkInEveryRun()
    {
        var path = new ActiveSimulationBotInputSource(WalkingOptions(), 3).SelectPath(
            1,
            JoinedSession("collision-revision"),
            new SimulationBotPosition(0f, 0f, -8f));
        var otherBot = new ActiveSimulationBotInputSource(WalkingOptions(), 4).SelectPath(
            1,
            JoinedSession("collision-revision"),
            new SimulationBotPosition(0f, 0f, -8f));

        var waypoint = Assert.Single(path!.Waypoints);
        Assert.Equal(2.3779707f, waypoint.X, 0.0001f);
        Assert.Equal(7.6748405f, waypoint.Z, 0.0001f);
        Assert.NotEqual(waypoint.X, Assert.Single(otherBot!.Waypoints).X);
    }

    // A 28 m ground with a wall across the middle, 8 m wide, between the bot and its goal.
    private static ChunkedStaticCollisionWorld WorldWithWall()
    {
        var bake = CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "development-world-1",
            ChunkSize = 32f,
            Boxes =
            [
                new CollisionBoxAuthoringEntry
                {
                    SourcePath = "Test/Ground#BoxCollider0",
                    LayerMask = CollisionLayers.CharacterMovement,
                    CenterY = -0.25f,
                    HalfExtentX = 15f,
                    HalfExtentY = 0.25f,
                    HalfExtentZ = 15f,
                    RotationW = 1f
                },
                new CollisionBoxAuthoringEntry
                {
                    SourcePath = "Test/Wall#BoxCollider0",
                    LayerMask = CollisionLayers.CharacterMovement,
                    CenterY = 1.5f,
                    HalfExtentX = 4f,
                    HalfExtentY = 1.5f,
                    HalfExtentZ = 0.5f,
                    RotationW = 1f
                }
            ]
        });
        return new ChunkedStaticCollisionWorld(
            bake.Manifest.WorldId,
            bake.Manifest.Revision,
            bake.Manifest.ChunkSize,
            bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
    }

    private static RealtimeJoinAccepted JoinedSession(string collisionRevision)
    {
        return new RealtimeJoinAccepted(
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            "Active Bot 1",
            "local-shard-1",
            "development-world-2",
            1,
            "movement-simulation",
            collisionRevision,
            "2026-10-20T00:00:00Z",
            "2026-10-20T00:01:00Z",
            false,
            new RealtimeCarryState(0, 0, 200),
            Movement,
            new RealtimePlayerState(0f, 0f, -8f, 0f, 0f, 0f, 0f, true),
            new RealtimeShardRules(false));
    }

    private static ActiveSimulationBotOptions WalkingOptions()
    {
        return ActiveSimulationBotOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ActiveSimulationBots:AuthorityUrl"] = "http://127.0.0.1:5000",
                    ["ActiveSimulationBots:AuthoritySecret"] = new string('s', 40),
                    ["ActiveSimulationBots:ShardId"] = "local-shard-1",
                    ["ActiveSimulationBots:IdleChance"] = "0"
                })
                .Build());
    }
}
