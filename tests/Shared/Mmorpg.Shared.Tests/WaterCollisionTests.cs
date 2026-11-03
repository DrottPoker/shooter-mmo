using System.IO;
using Mmorpg.GameSimulation;

namespace Mmorpg.Shared.Tests;

// A round lake in a terrain basin: the ground lies at 0 outside a radius of 12 m and falls
// 0.3 m per meter toward the center, down to -3 m. The surface lies at -0.2 m, so the
// shoreline is at a radius of about 11.33 m, the wading depth of 0.5 m at 9.67 m, and the
// depth limit of 1.2 m at 7.33 m. The surface square reaches under the land around it.
public sealed class WaterCollisionTests
{
    private const float Surface = -0.2f;
    private const float LimitRadius = 12f - ((WaterRules.BlockDepth - Surface) / 0.3f);
    private const string LakeId = "test.lake";

    private static readonly MovementSimulationSettings Settings = new(
        30,
        5f,
        720f,
        -24f,
        -2f,
        0f,
        -30f,
        30f,
        -30f,
        30f,
        CharacterCollisionSettings.Default);

    private static readonly Lazy<ChunkedStaticCollisionWorld> Lake = new(() => CompileWorld(LakeAuthoring()));

    [Fact]
    public void WaterSurfacesLieOnTheWaterLayerAndNeverPush()
    {
        var buffer = new CollisionQueryBuffer();
        Lake.Value.QueryShapes(PointBounds(3f, Surface, -1f), CollisionLayers.All, buffer);

        // Both triangles of the square reach the point with their bounds.
        var surfaces = buffer.Shapes.Where(shape => (shape.LayerMask & CollisionLayers.Water) != 0).ToArray();
        Assert.Equal(2, surfaces.Length);
        Assert.All(surfaces, shape =>
        {
            var surface = Assert.IsType<CollisionWaterSurface>(shape);
            Assert.Equal(CollisionLayers.Water, surface.LayerMask);
            Assert.Equal(LakeId, surface.BodyId);
            Assert.False(surface.TryGetCapsulePenetration(new SimulationVector3(3f, -1f, -1f), 0.35f, 2f, out _, out _));
        });

        var movement = new CollisionQueryBuffer();
        Lake.Value.QueryShapes(PointBounds(3f, Surface, -1f), CollisionLayers.CharacterMovement, movement);
        Assert.DoesNotContain(movement.Shapes, shape => shape is CollisionWaterSurface);
    }

    [Fact]
    public void DeepWaterEdgesStandWhereTheWaterReachesTheDepthLimitAndFaceTheShallowSide()
    {
        var edges = Edges(Lake.Value);

        Assert.NotEmpty(edges);
        foreach (var edge in edges)
        {
            Assert.Equal(CollisionLayers.CharacterMovement | CollisionLayers.DeepWaterEdge, edge.LayerMask);
            Assert.True(MathF.Abs(edge.Normal.Y) < 0.001f, $"Edge {edge.StableId} is not vertical.");
            var centerX = (edge.A.X + edge.B.X + edge.C.X) / 3f;
            var centerZ = (edge.A.Z + edge.B.Z + edge.C.Z) / 3f;
            var radius = MathF.Sqrt((centerX * centerX) + (centerZ * centerZ));
            Assert.InRange(radius, LimitRadius - 0.3f, LimitRadius + 0.3f);

            // The shallow side lies outward, away from the center.
            Assert.True((edge.Normal.X * centerX) + (edge.Normal.Z * centerZ) > 0f, $"Edge {edge.StableId} faces the deep side.");
            Assert.InRange(MathF.Min(edge.A.Y, MathF.Min(edge.B.Y, edge.C.Y)), Surface - 1.71f, Surface - 1.69f);
            Assert.InRange(MathF.Max(edge.A.Y, MathF.Max(edge.B.Y, edge.C.Y)), Surface + 0.29f, Surface + 0.31f);
        }
    }

    [Fact]
    public void AWalkerStopsWhereTheWaterGetsTooDeep()
    {
        var state = Walk(Lake.Value, new SimulationVector3(-16f, 0f, 0f), new SimulationVector3(0f, 0f, 0f), 600);

        var radius = MathF.Abs(state.PositionX);
        Assert.InRange(radius, LimitRadius, LimitRadius + 0.5f);
        Assert.True(WaterRules.TryGetDepth(Lake.Value, state.Position, new CollisionQueryBuffer(), out var depth));
        Assert.InRange(depth, 1f, WaterRules.BlockDepth);
    }

    [Fact]
    public void AnyDirectionIntoTheLakeStopsAtTheEdge()
    {
        for (var angle = 0; angle < 360; angle += 15)
        {
            var radians = angle * MathF.PI / 180f;
            var start = new SimulationVector3(16f * MathF.Cos(radians), 0f, 16f * MathF.Sin(radians));
            var state = Walk(Lake.Value, start, new SimulationVector3(0f, 0f, 0f), 600);
            var radius = MathF.Sqrt((state.PositionX * state.PositionX) + (state.PositionZ * state.PositionZ));
            Assert.True(radius > LimitRadius - 0.1f, $"From {angle} degrees the walker reached a radius of {radius}.");
        }
    }

    [Fact]
    public void WadingDepthSlowsTheWalkAndShallowWaterDoesNot()
    {
        var land = FirstStep(-16f, -20f);
        var shallow = FirstStep(-10.8f, -14f);
        var wading = FirstStep(-8.5f, -14f);

        var fullStep = Settings.WalkSpeed / Settings.TickRateHz;
        Assert.Equal(fullStep, land, 3);
        Assert.Equal(fullStep, shallow, 3);
        Assert.Equal(fullStep * WaterRules.WadeSpeedMultiplier, wading, 3);
    }

    [Fact]
    public void ACharacterInDeepWaterWalksOutThroughTheBackOfTheEdge()
    {
        var state = Walk(Lake.Value, new SimulationVector3(-2f, -3f, 0f), new SimulationVector3(-16f, 0f, 0f), 600);

        Assert.Equal(-16f, state.PositionX, 2);
        Assert.Equal(0f, state.PositionY, 2);
    }

    [Fact]
    public void APlannedRouteAcrossTheLakeGoesAroundTheDeepWater()
    {
        var start = new SimulationVector3(-16f, 0f, 0f);
        var destination = new SimulationVector3(16f, 0f, 0f);
        var plan = new MovementPathPlanner(Lake.Value, Settings, MovementPathPlannerSettings.Default).Plan(start, destination);

        Assert.Equal(MovementPathPlanStatus.Reached, plan.Status);
        var path = new MovementPath(1, plan.Waypoints.ToArray());
        var state = PlayerMovementSimulation.CreateInitialState(Settings, Lake.Value, start.X, start.Y, start.Z, 0f);
        var buffer = new CollisionQueryBuffer();
        for (uint tick = 1; tick <= 3_000 && state.PathProgress.NextWaypointIndex < path.Count; tick++)
        {
            state = PlayerMovementSimulation.Step(state, new PlayerMovementInput(tick, tick, path), Settings, PlayerCarryState.Default, Lake.Value);
            Assert.True(
                !WaterRules.TryGetDepth(Lake.Value, state.Position, buffer, out var depth) || depth <= WaterRules.BlockDepth,
                $"The route entered deep water at ({state.PositionX}, {state.PositionZ}).");
        }

        Assert.Equal(16f, state.PositionX, 2);
    }

    // A pond 0.8 m deep on flat ground between the start and the destination: wading
    // straight through takes as long as 22.7 m of dry land, the way around it 18.5 m.
    [Fact]
    public void ARoutePastAWadingPondGoesAroundItOverLand()
    {
        var world = CompileWorld(FlatWaterAuthoring(Rectangle("Test/Pond", "test.pond", -5f, 5f, -3f, 3f, 0.8f)));
        var start = new SimulationVector3(-8f, 0f, 0f);
        var plan = new MovementPathPlanner(world, Settings, MovementPathPlannerSettings.Default)
            .Plan(start, new SimulationVector3(8f, 0f, 0f));

        Assert.Equal(MovementPathPlanStatus.Reached, plan.Status);
        var deepest = FollowRoute(world, start, plan, out var end);
        Assert.Equal(8f, end.PositionX, 2);
        Assert.True(deepest < WaterRules.WadeDepth, $"The route waded {deepest} m deep.");
    }

    // A stream 0.8 m deep and 4 m wide runs 60 m to either side, so crossing it is far
    // faster than walking around it.
    [Fact]
    public void ARouteWadesAcrossWaterWhereGoingAroundTakesLonger()
    {
        var world = CompileWorld(FlatWaterAuthoring(Rectangle("Test/Stream", "test.stream", -2f, 2f, -60f, 60f, 0.8f)));
        var start = new SimulationVector3(-6f, 0f, 0f);
        var plan = new MovementPathPlanner(world, Settings, MovementPathPlannerSettings.Default)
            .Plan(start, new SimulationVector3(6f, 0f, 1f));

        Assert.Equal(MovementPathPlanStatus.Reached, plan.Status);
        Assert.Single(plan.Waypoints);
        var deepest = FollowRoute(world, start, plan, out var end);
        Assert.Equal(6f, end.PositionX, 2);
        Assert.InRange(deepest, WaterRules.WadeDepth, WaterRules.BlockDepth);
    }

    [Fact]
    public void TheDepthQueryNamesTheWaterBodyAboveTheFeet()
    {
        var buffer = new CollisionQueryBuffer();

        Assert.True(WaterRules.TryGetDepth(Lake.Value, new SimulationVector3(-10f, Ground(-10f), 0f), buffer, out _, out var bodyId));
        Assert.Equal(LakeId, bodyId);
        Assert.False(WaterRules.TryGetDepth(Lake.Value, new SimulationVector3(-16f, 0f, 0f), buffer, out _, out bodyId));
        Assert.Null(bodyId);
    }

    [Fact]
    public void AClickIntoDeepWaterPlansToTheNearestReachableShore()
    {
        var plan = new MovementPathPlanner(Lake.Value, Settings, MovementPathPlannerSettings.Default)
            .Plan(new SimulationVector3(-16f, 0f, 0f), new SimulationVector3(0f, -3f, 0f));

        Assert.Equal(MovementPathPlanStatus.Closest, plan.Status);
        var end = plan.Waypoints[^1];
        var radius = MathF.Sqrt((end.X * end.X) + (end.Z * end.Z));
        Assert.InRange(radius, LimitRadius, LimitRadius + 1.5f);
    }

    [Fact]
    public void ABridgeOverDeepWaterIsWalkedAtFullSpeed()
    {
        // A deck 0.3 m high across the lake, 0.5 m above the water and above the edges.
        var world = CompileWorld(LakeAuthoring(Box("Test/Bridge", 0f, 0.15f, 0f, 16f, 0.15f, 1.5f)));

        var state = Walk(world, new SimulationVector3(-18f, 0f, 0f), new SimulationVector3(18f, 0f, 0f), 600);

        Assert.Equal(18f, state.PositionX, 2);
        Assert.False(WaterRules.TryGetDepth(world, new SimulationVector3(0f, 0.3f, 0f), new CollisionQueryBuffer(), out _));
        Assert.NotEmpty(Edges(world));
    }

    [Fact]
    public void GroundBetweenTheSurfaceAndTheFeetKeepsTheWaterOut()
    {
        // A pond 0.5 m deep on a raised bed, over a floor 2.5 m below the bed.
        var world = CompileWorld(new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "water-tests",
            ChunkSize = 32f,
            Boxes =
            [
                Box("Test/Floor", 0f, -0.25f, 0f, 20f, 0.25f, 20f),
                Box("Test/Bed", 0f, 2f, 0f, 6f, 0.5f, 6f)
            ],
            Water = [Square("Test/Pond", "test.pond", 5f, 3f)]
        });
        var buffer = new CollisionQueryBuffer();

        Assert.True(WaterRules.TryGetDepth(world, new SimulationVector3(0f, 2.5f, 0f), buffer, out var depth));
        Assert.Equal(0.5f, depth, 4);
        Assert.False(WaterRules.TryGetDepth(world, new SimulationVector3(0f, 0f, 0f), buffer, out _));
        Assert.False(WaterRules.TryGetDepth(world, new SimulationVector3(10f, 0f, 0f), buffer, out _));
    }

    // A surface drawn into a bank is dry land for the depth query as for the edges: a box bank
    // from 2.5 to 3.5 m, and a terrain bank at 4 m, each over a passage at 0 m, and a pond
    // surface at 3 m that reaches into both.
    [Fact]
    public void WaterDrawnIntoABankIsDryLandBelowIt()
    {
        var world = CompileWorld(new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "water-tests",
            ChunkSize = 32f,
            Boxes =
            [
                Box("Test/Floor", 0f, -0.25f, 0f, 20f, 0.25f, 20f),
                Box("Test/BoxBank", -3f, 3f, 0f, 2f, 0.5f, 2f)
            ],
            Heightfields =
            [
                new CollisionHeightfieldAuthoringEntry
                {
                    SourcePath = "Test/TerrainBank#TerrainCollider0",
                    LayerMask = CollisionLayers.CharacterMovement,
                    OriginX = 2f,
                    OriginZ = -2f,
                    SpacingX = 1f,
                    SpacingZ = 1f,
                    SamplesX = 5,
                    SamplesZ = 5,
                    Heights = CollisionAuthoringEncoding.EncodeFloats(Enumerable.Repeat(4f, 25).ToArray()),
                    Holes = string.Empty
                }
            ],
            Water = [Square("Test/Pond", "test.pond", 5f, 3f)]
        });
        var buffer = new CollisionQueryBuffer();

        Assert.False(WaterRules.TryGetDepth(world, new SimulationVector3(-3f, 0f, 0f), buffer, out _));
        Assert.False(WaterRules.TryGetDepth(world, new SimulationVector3(4f, 0f, 0f), buffer, out _));
        Assert.True(WaterRules.TryGetDepth(world, new SimulationVector3(0f, 0f, 0f), buffer, out var depth));
        Assert.Equal(3f, depth, 4);
    }

    [Fact]
    public void WaterSurfacesKeepTheirBodyThroughTheChunkCodec()
    {
        var bake = CollisionWorldCompiler.Compile(LakeAuthoring());
        var chunk = CollisionChunkCodec.Decode(bake.Chunks[0].Data);

        Assert.Equal(bake.Chunks[0].Data, CollisionChunkCodec.Encode(chunk));
        Assert.All(chunk.Shapes.OfType<CollisionWaterSurface>(), surface => Assert.Equal(LakeId, surface.BodyId));
        Assert.Equal(bake.Manifest.Revision, CollisionWorldCompiler.Compile(LakeAuthoring()).Manifest.Revision);
    }

    [Fact]
    public void ChunkCodecRejectsAWaterSurfaceWithAMissingBody()
    {
        var chunk = new CollisionChunk(
            0,
            0,
            new CollisionAabb(new SimulationVector3(0f, float.MinValue, 0f), new SimulationVector3(32f, float.MaxValue, 32f)),
            [
                new CollisionWaterSurface(
                    7,
                    "test.pond",
                    new SimulationVector3(1f, 0f, 1f),
                    new SimulationVector3(1f, 0f, 2f),
                    new SimulationVector3(2f, 0f, 1f))
            ]);
        var data = CollisionChunkCodec.Encode(chunk);

        // The body index follows the surface's stable id, 40 bytes before the end.
        data[^40] = 5;

        Assert.Throws<InvalidDataException>(() => CollisionChunkCodec.Decode(data));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Lake")]
    [InlineData("lake one")]
    public void CompilerRejectsAnInvalidWaterBodyId(string bodyId)
    {
        var authoring = LakeAuthoring();
        authoring.Water[0].BodyId = bodyId;

        Assert.Throws<InvalidDataException>(() => CollisionWorldCompiler.Compile(authoring));
    }

    [Fact]
    public void CompilerRejectsAWaterTriangleThatDoesNotFaceUp()
    {
        var authoring = LakeAuthoring();
        authoring.Water[0].Triangles = CollisionAuthoringEncoding.EncodeInts([0, 2, 1, 0, 3, 2]);

        Assert.Throws<InvalidDataException>(() => CollisionWorldCompiler.Compile(authoring));
    }

    [Fact]
    public void AWorldWithoutWaterHasNoEdges()
    {
        var authoring = LakeAuthoring();
        authoring.Water = [];

        Assert.Empty(Edges(CompileWorld(authoring)));
    }

    // The first step from a point on the X axis toward another one.
    private static float FirstStep(float startX, float destinationX)
    {
        var state = PlayerMovementSimulation.CreateInitialState(Settings, Lake.Value, startX, Ground(startX), 0f, 0f);
        Assert.True(state.IsGrounded);
        var next = PlayerMovementSimulation.Step(
            state,
            new PlayerMovementInput(1, 1, new MovementPath(1, [new SimulationVector3(destinationX, Ground(destinationX), 0f)])),
            Settings,
            PlayerCarryState.Default,
            Lake.Value);
        var deltaX = next.PositionX - state.PositionX;
        var deltaZ = next.PositionZ - state.PositionZ;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    // The basin height on the X axis, where the terrain follows the profile exactly.
    private static float Ground(float x)
    {
        return MathF.Max(-3f, MathF.Min(0f, -0.3f * (12f - MathF.Abs(x))));
    }

    private static PlayerMovementState Walk(
        ICollisionWorld world,
        SimulationVector3 start,
        SimulationVector3 destination,
        int ticks)
    {
        var path = new MovementPath(1, [destination]);
        var state = PlayerMovementSimulation.CreateInitialState(Settings, world, start.X, start.Y, start.Z, 0f);
        for (uint tick = 1; tick <= ticks; tick++)
        {
            state = PlayerMovementSimulation.Step(state, new PlayerMovementInput(tick, tick, path), Settings, PlayerCarryState.Default, world);
        }

        return state;
    }

    // Walks a planned route to its end and returns the deepest water on the way.
    private static float FollowRoute(
        ICollisionWorld world,
        SimulationVector3 start,
        MovementPathPlan plan,
        out PlayerMovementState state)
    {
        var path = new MovementPath(1, plan.Waypoints.ToArray());
        state = PlayerMovementSimulation.CreateInitialState(Settings, world, start.X, start.Y, start.Z, 0f);
        var buffer = new CollisionQueryBuffer();
        var deepest = 0f;
        for (uint tick = 1; tick <= 3_000 && state.PathProgress.NextWaypointIndex < path.Count; tick++)
        {
            state = PlayerMovementSimulation.Step(state, new PlayerMovementInput(tick, tick, path), Settings, PlayerCarryState.Default, world);
            if (WaterRules.TryGetDepth(world, state.Position, buffer, out var depth))
            {
                deepest = MathF.Max(deepest, depth);
            }
        }

        return deepest;
    }

    // Flat ground at 0 m with the given water on it.
    private static CollisionWorldAuthoringDocument FlatWaterAuthoring(CollisionWaterAuthoringEntry water)
    {
        return new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "water-tests",
            ChunkSize = 32f,
            Boxes = [Box("Test/Floor", 0f, -0.25f, 0f, 30f, 0.25f, 70f)],
            Water = [water]
        };
    }

    private static CollisionTriangle[] Edges(ICollisionWorld world)
    {
        var buffer = new CollisionQueryBuffer();
        world.QueryShapes(
            new CollisionAabb(new SimulationVector3(-30f, -10f, -30f), new SimulationVector3(30f, 10f, 30f)),
            CollisionLayers.DeepWaterEdge,
            buffer);
        return buffer.Shapes.OfType<CollisionTriangle>().ToArray();
    }

    private static CollisionAabb PointBounds(float x, float y, float z)
    {
        var point = new SimulationVector3(x, y, z);
        return new CollisionAabb(point, point);
    }

    private static ChunkedStaticCollisionWorld CompileWorld(CollisionWorldAuthoringDocument authoring)
    {
        var bake = CollisionWorldCompiler.Compile(authoring);
        return new ChunkedStaticCollisionWorld(
            bake.Manifest.WorldId,
            bake.Manifest.Revision,
            bake.Manifest.ChunkSize,
            bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
    }

    private static CollisionWorldAuthoringDocument LakeAuthoring(params CollisionBoxAuthoringEntry[] boxes)
    {
        const int samples = 41;
        var heights = new float[samples * samples];
        for (var z = 0; z < samples; z++)
        {
            for (var x = 0; x < samples; x++)
            {
                var offsetX = x - 20f;
                var offsetZ = z - 20f;
                var radius = MathF.Sqrt((offsetX * offsetX) + (offsetZ * offsetZ));
                heights[(z * samples) + x] = MathF.Max(-3f, MathF.Min(0f, -0.3f * (12f - radius)));
            }
        }

        return new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "water-tests",
            ChunkSize = 32f,
            Boxes = boxes,
            Meshes = [],
            Heightfields =
            [
                new CollisionHeightfieldAuthoringEntry
                {
                    SourcePath = "Test/Basin#TerrainCollider0",
                    LayerMask = CollisionLayers.CharacterMovement,
                    OriginX = -20f,
                    OriginZ = -20f,
                    SpacingX = 1f,
                    SpacingZ = 1f,
                    SamplesX = samples,
                    SamplesZ = samples,
                    Heights = CollisionAuthoringEncoding.EncodeFloats(heights),
                    Holes = string.Empty
                }
            ],
            Water = [Square("Test/Lake#WaterBody", LakeId, 14f, Surface)]
        };
    }

    // A square surface around the origin, clockwise seen from above so it faces up.
    private static CollisionWaterAuthoringEntry Square(string sourcePath, string bodyId, float halfSize, float height)
    {
        return Rectangle(sourcePath, bodyId, -halfSize, halfSize, -halfSize, halfSize, height);
    }

    private static CollisionWaterAuthoringEntry Rectangle(
        string sourcePath,
        string bodyId,
        float minimumX,
        float maximumX,
        float minimumZ,
        float maximumZ,
        float height)
    {
        return new CollisionWaterAuthoringEntry
        {
            SourcePath = sourcePath,
            BodyId = bodyId,
            Vertices = CollisionAuthoringEncoding.EncodeFloats(
            [
                minimumX, height, minimumZ,
                minimumX, height, maximumZ,
                maximumX, height, maximumZ,
                maximumX, height, minimumZ
            ]),
            Triangles = CollisionAuthoringEncoding.EncodeInts([0, 1, 2, 0, 2, 3])
        };
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
