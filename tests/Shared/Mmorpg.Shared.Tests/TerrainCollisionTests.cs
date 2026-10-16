using Mmorpg.GameSimulation;

namespace Mmorpg.Shared.Tests;

public sealed class TerrainCollisionTests
{
    private static readonly MovementSimulationSettings Settings = new(
        30,
        5f,
        720f,
        -24f,
        -2f,
        0f,
        -60f,
        60f,
        -60f,
        60f,
        CharacterCollisionSettings.Default);

    private static readonly SimulationVector3 Down = new(0f, -1f, 0f);

    [Fact]
    public void HeightfieldBecomesTwoUpwardTrianglesPerCellExceptHoles()
    {
        // Cell 5 is cell (1, 1) of a 4 by 3 cell grid and covers X -1 to 0 and Z -1 to 0.
        var heightfield = Heightfield(
            "Test/Hills",
            -2f,
            -2f,
            1f,
            5,
            4,
            (x, z) => (0.25f * x) + (0.5f * z) + 2f,
            cell => cell == 5);

        var triangles = heightfield.ToCollisionTriangles().ToArray();

        Assert.Equal(((4 * 3) - 1) * 2, triangles.Length);
        Assert.All(triangles, triangle => Assert.True(triangle.Normal.Y > 0.8f));
        Assert.Equal(triangles.Length, triangles.Select(triangle => triangle.StableId).Distinct().Count());
        var world = CompileWorld(heightfields: [heightfield]);
        Assert.False(RaycastDown(world, -0.5f, -0.5f, out _));
        Assert.True(RaycastDown(world, 0.3f, 0.7f, out var hit));
        Assert.Equal((0.25f * 0.3f) + (0.5f * 0.7f) + 2f, hit.Point.Y, 4);
    }

    [Fact]
    public void RaysThroughSharedEdgesAndCornersAlwaysHitTheSurface()
    {
        var world = CompileWorld(heightfields:
        [
            Heightfield("Test/Flat", -4f, -4f, 1f, 9, 9, (_, _) => 1f)
        ]);

        for (var z = -3.5f; z <= 3.5f; z += 0.25f)
        {
            for (var x = -3.5f; x <= 3.5f; x += 0.25f)
            {
                Assert.True(RaycastDown(world, x, z, out var hit), $"Missed the surface at ({x}, {z}).");
                Assert.Equal(1f, hit.Point.Y, 5);
            }
        }
    }

    [Fact]
    public void TrianglesCollideOnlyFromTheirFront()
    {
        // Clockwise seen from above, so the triangle faces up.
        var triangle = new CollisionTriangle(
            1,
            CollisionLayers.CharacterMovement,
            new SimulationVector3(-1f, 0f, -1f),
            new SimulationVector3(-1f, 0f, 2f),
            new SimulationVector3(2f, 0f, -1f));
        var radius = CharacterCollisionSettings.Default.Radius;
        var height = CharacterCollisionSettings.Default.Height;

        Assert.Equal(1f, triangle.Normal.Y, 6);
        Assert.True(triangle.TryRaycast(new SimulationVector3(0f, 5f, 0f), Down, 10f, out var downHit));
        Assert.Equal(5f, downHit.Distance, 5);
        Assert.False(triangle.TryRaycast(new SimulationVector3(0f, -5f, 0f), new SimulationVector3(0f, 1f, 0f), 10f, out _));

        Assert.True(triangle.TryGetCapsulePenetration(
            new SimulationVector3(0f, -0.2f, 0f),
            radius,
            height,
            out var frontNormal,
            out var frontDepth));
        Assert.Equal(1f, frontNormal.Y, 4);
        Assert.Equal(0.2f, frontDepth, 3);

        Assert.False(triangle.TryGetCapsulePenetration(
            new SimulationVector3(0f, -3f, 0f),
            radius,
            height,
            out _,
            out _));

        // The capsule axis crosses the triangle, so it is pushed fully out to the front.
        Assert.True(triangle.TryGetCapsulePenetration(
            new SimulationVector3(0f, -0.5f, 0f),
            radius,
            height,
            out var crossingNormal,
            out var crossingDepth));
        Assert.Equal(1f, crossingNormal.Y, 4);
        Assert.Equal(radius + 0.15f, crossingDepth, 3);
    }

    [Fact]
    public void MeshKeepsItsWindingAndSkipsDegenerateTriangles()
    {
        var mesh = Mesh(
            "Test/Roof",
            [
                new(0f, 2f, 0f),
                new(0f, 2f, 1f),
                new(1f, 2f, 0f),
                new(2f, 2f, 0f)
            ],
            [0, 1, 2, 0, 2, 3, 0, 2, 1]);

        var triangles = mesh.ToCollisionTriangles().ToArray();

        Assert.Equal(2, triangles.Length);
        Assert.Equal(1f, triangles[0].Normal.Y, 6);
        Assert.Equal(-1f, triangles[1].Normal.Y, 6);
        Assert.NotEqual(triangles[0].StableId, triangles[1].StableId);
        Assert.Throws<InvalidDataException>(() => Mesh(
            "Test/Broken",
            [new(0f, 0f, 0f), new(0f, 0f, 1f), new(1f, 0f, 0f)],
            [0, 1, 3]).ToCollisionTriangles().ToArray());
    }

    [Fact]
    public void WallTriangleLyingOnAChunkEdgeIsBakedAndBlocks()
    {
        // The wall has no width in X and lies exactly on the edge between two chunks.
        var world = CompileWorld(
            [Box("Test/Ground", 0f, -0.25f, 0f, 50f, 0.25f, 50f)],
            [
                Mesh(
                    "Test/EdgeWall",
                    [new(32f, -0.1f, -4f), new(32f, 3f, -4f), new(32f, 3f, 4f), new(32f, -0.1f, 4f)],
                    [0, 1, 2, 0, 2, 3])
            ]);

        var state = Walk(world, new SimulationVector3(36f, 0f, 0f), new SimulationVector3(28f, 0f, 0f), 60);

        Assert.Equal(32f + CharacterCollisionSettings.Default.Radius, state.PositionX, 2);
    }

    [Fact]
    public void ChunkCodecRoundTripsBoxesAndTriangles()
    {
        var bake = CollisionWorldCompiler.Compile(Authoring(
            [Box("Test/Ground", 8f, -0.25f, 8f, 8f, 0.25f, 8f)],
            [Mesh("Test/Ceiling", [new(2f, 4f, 2f), new(6f, 4f, 2f), new(2f, 4f, 6f)], [0, 1, 2])],
            [Heightfield("Test/Hill", 4f, 4f, 2f, 5, 5, (x, z) => 0.1f * ((x * x) + (z * z)))]));

        var chunk = Assert.Single(bake.Chunks);
        var decoded = CollisionChunkCodec.Decode(chunk.Data);

        Assert.Equal(1 + 1 + 32, decoded.Shapes.Count);
        Assert.Single(decoded.Shapes.OfType<CollisionBox>());
        var ceiling = decoded.Shapes.OfType<CollisionTriangle>().Single(triangle => triangle.Normal.Y < 0f);
        Assert.Equal(new SimulationVector3(6f, 4f, 2f), ceiling.B);
        Assert.Equal(chunk.Data, CollisionChunkCodec.Encode(decoded));
        Assert.Equal(bake.Manifest.Revision, CollisionWorldCompiler.Compile(Authoring(
            [Box("Test/Ground", 8f, -0.25f, 8f, 8f, 0.25f, 8f)],
            [Mesh("Test/Ceiling", [new(2f, 4f, 2f), new(6f, 4f, 2f), new(2f, 4f, 6f)], [0, 1, 2])],
            [Heightfield("Test/Hill", 4f, 4f, 2f, 5, 5, (x, z) => 0.1f * ((x * x) + (z * z)))])).Manifest.Revision);
    }

    [Fact]
    public void IndexedChunkQueriesMatchALinearScan()
    {
        var random = new Random(1234);
        var shapes = new List<CollisionShape>
        {
            new CollisionBox(
                1,
                CollisionLayers.CharacterMovement,
                new SimulationVector3(16f, -0.25f, 16f),
                new SimulationVector3(16f, 0.25f, 16f),
                SimulationQuaternion.Identity)
        };
        for (var index = 0; index < 400; index++)
        {
            var x = (float)(random.NextDouble() * 29d);
            var z = (float)(random.NextDouble() * 29d);
            var size = (float)(random.NextDouble() * 3d) + 0.1f;
            shapes.Add(new CollisionTriangle(
                (ulong)(index + 2),
                index % 3 == 0 ? CollisionLayers.CombatQueries : CollisionLayers.CharacterMovement,
                new SimulationVector3(x, 0f, z),
                new SimulationVector3(x, 1f, z + size),
                new SimulationVector3(x + size, 0.5f, z)));
        }

        var chunk = new CollisionChunk(
            0,
            0,
            new CollisionAabb(
                new SimulationVector3(0f, float.MinValue, 0f),
                new SimulationVector3(32f, float.MaxValue, 32f)),
            shapes);
        var world = new ChunkedStaticCollisionWorld("index-test", "revision", 32f, [chunk]);
        var buffer = new CollisionQueryBuffer();
        for (var query = 0; query < 500; query++)
        {
            var minimum = new SimulationVector3(
                (float)(random.NextDouble() * 26d),
                (float)(random.NextDouble() * 2d) - 0.5f,
                (float)(random.NextDouble() * 26d));
            var bounds = new CollisionAabb(
                minimum,
                minimum + new SimulationVector3(
                    (float)(random.NextDouble() * 6d),
                    (float)random.NextDouble(),
                    (float)(random.NextDouble() * 6d)));
            buffer.Clear();
            world.QueryShapes(bounds, CollisionLayers.CharacterMovement, buffer);

            var expected = shapes
                .Where(shape => (shape.LayerMask & CollisionLayers.CharacterMovement) != 0
                    && shape.Bounds.Intersects(bounds))
                .Select(shape => shape.StableId)
                .Order();
            Assert.Equal(expected, buffer.Shapes.Select(shape => shape.StableId).Order());
        }
    }

    [Fact]
    public void CharacterWalksUpAWalkableHeightfieldSlope()
    {
        // Flat until X 0, then rising at about 26.6 degrees.
        var world = CompileWorld(heightfields:
        [
            Heightfield("Test/Slope", -10f, -5f, 1f, 31, 11, (x, _) => 0.5f * Math.Max(0f, x))
        ]);

        var state = Walk(world, new SimulationVector3(-5f, 0f, 0f), new SimulationVector3(12f, 0f, 0f), 150);

        Assert.Equal(12f, state.PositionX, 2);
        Assert.Equal(6f, state.PositionY, 1);
        Assert.True(state.IsGrounded);
    }

    [Fact]
    public void SteepHeightfieldSlopeBlocksTheCharacterLikeAWall()
    {
        // Flat until X 0, then rising at about 63 degrees.
        var world = CompileWorld(heightfields:
        [
            Heightfield("Test/Cliff", -10f, -5f, 1f, 31, 11, (x, _) => 2f * Math.Max(0f, x))
        ]);

        var state = Walk(world, new SimulationVector3(-5f, 0f, 0f), new SimulationVector3(12f, 0f, 0f), 150);

        Assert.InRange(state.PositionX, -1f, 0.5f);
        Assert.True(state.PositionY < 1f);
        Assert.True(state.IsGrounded);
    }

    [Fact]
    public void TunnelCeilingAndTerrainAboveItEachCollideOnlyFromTheirOwnSide()
    {
        var world = TunnelWorld();

        var through = Walk(world, new SimulationVector3(0f, 0f, -12f), new SimulationVector3(0f, 0f, 12f), 180);
        Assert.Equal(12f, through.PositionZ, 2);
        Assert.Equal(0f, through.PositionY, 3);

        var over = Walk(world, new SimulationVector3(0f, 6f, -6f), new SimulationVector3(0f, 6f, 6f), 90);
        Assert.Equal(6f, over.PositionZ, 2);
        Assert.Equal(6f, over.PositionY, 3);
        Assert.True(over.IsGrounded);
    }

    [Fact]
    public void PlannerUsesTheTunnelBelowAndTheTerrainAbove()
    {
        var world = TunnelWorld();
        var planner = new MovementPathPlanner(world, Settings, MovementPathPlannerSettings.Default);

        var below = planner.Plan(new SimulationVector3(0f, 0f, -12f), new SimulationVector3(0f, 0f, 12f));
        var above = planner.Plan(new SimulationVector3(0f, 6f, -6f), new SimulationVector3(3f, 6f, 6f));

        Assert.Equal(MovementPathPlanStatus.Reached, below.Status);
        Assert.Equal(0f, below.Waypoints[^1].Y, 3);
        Assert.Equal(MovementPathPlanStatus.Reached, above.Status);
        Assert.Equal(6f, above.Waypoints[^1].Y, 3);
    }

    [Fact]
    public void PlannerClimbsARidgeByItsGentleEnd()
    {
        // A 4 m high ridge with 63 degree faces to the north, south, and west, and an
        // 18 degree slope down to the east.
        var world = CompileWorld(
            [Box("Test/Ground", 0f, -0.25f, 0f, 30f, 0.25f, 30f)],
            heightfields:
            [
                Heightfield(
                    "Test/Ridge",
                    -8f,
                    -8f,
                    1f,
                    29,
                    17,
                    (x, z) => Math.Max(
                        0.01f,
                        Math.Min(
                            4f,
                            Math.Min(
                                4f - (2f * (Math.Max(Math.Abs(z), -x) - 2f)),
                                4f - (0.33f * (x - 2f))))))
            ]);
        var start = new SimulationVector3(0f, 0f, -6f);

        var plan = new MovementPathPlanner(world, Settings, MovementPathPlannerSettings.Default)
            .Plan(start, new SimulationVector3(0f, 4f, 0f));

        Assert.Equal(MovementPathPlanStatus.Reached, plan.Status);
        Assert.Contains(plan.Waypoints, waypoint => waypoint.X > 8f);
        var state = Walk(world, start, plan.Waypoints.ToArray(), 600);
        Assert.Equal(0f, state.PositionX, 1);
        Assert.Equal(0f, state.PositionZ, 1);
        Assert.Equal(4f, state.PositionY, 1);
    }

    private static ChunkedStaticCollisionWorld TunnelWorld()
    {
        // A sheet of terrain at 6 m faces up, and a tunnel ceiling at 4 m below it faces
        // down, so both cover the same area.
        return CompileWorld(
            [Box("Test/Ground", 0f, -0.25f, 0f, 30f, 0.25f, 30f)],
            [
                Mesh(
                    "Test/TunnelCeiling",
                    [new(-8f, 4f, -8f), new(8f, 4f, -8f), new(8f, 4f, 8f), new(-8f, 4f, 8f)],
                    [0, 1, 2, 0, 2, 3])
            ],
            [Heightfield("Test/Plateau", -8f, -8f, 1f, 17, 17, (_, _) => 6f)]);
    }

    private static PlayerMovementState Walk(
        ICollisionWorld world,
        SimulationVector3 start,
        SimulationVector3 destination,
        int ticks)
    {
        return Walk(world, start, [destination], ticks);
    }

    private static PlayerMovementState Walk(
        ICollisionWorld world,
        SimulationVector3 start,
        SimulationVector3[] waypoints,
        int ticks)
    {
        var path = new MovementPath(1, waypoints);
        var state = PlayerMovementSimulation.CreateInitialState(Settings, world, start.X, start.Y, start.Z, 0f);
        for (uint tick = 1; tick <= ticks; tick++)
        {
            state = PlayerMovementSimulation.Step(
                state,
                new PlayerMovementInput(tick, tick, path),
                Settings,
                PlayerCarryState.Default,
                world);
        }

        return state;
    }

    private static bool RaycastDown(ICollisionWorld world, float x, float z, out CollisionRayHit hit)
    {
        return CollisionRaycast.TryRaycast(
            world,
            new SimulationVector3(x, 50f, z),
            Down,
            100f,
            CollisionLayers.CharacterMovement,
            new CollisionQueryBuffer(),
            out hit);
    }

    private static ChunkedStaticCollisionWorld CompileWorld(
        CollisionBoxAuthoringEntry[]? boxes = null,
        CollisionMeshAuthoringEntry[]? meshes = null,
        CollisionHeightfieldAuthoringEntry[]? heightfields = null)
    {
        var bake = CollisionWorldCompiler.Compile(Authoring(boxes, meshes, heightfields));
        return new ChunkedStaticCollisionWorld(
            bake.Manifest.WorldId,
            bake.Manifest.Revision,
            bake.Manifest.ChunkSize,
            bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
    }

    private static CollisionWorldAuthoringDocument Authoring(
        CollisionBoxAuthoringEntry[]? boxes,
        CollisionMeshAuthoringEntry[]? meshes,
        CollisionHeightfieldAuthoringEntry[]? heightfields)
    {
        return new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "terrain-tests",
            ChunkSize = 32f,
            Boxes = boxes ?? [],
            Meshes = meshes ?? [],
            Heightfields = heightfields ?? []
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

    private static CollisionMeshAuthoringEntry Mesh(
        string sourcePath,
        SimulationVector3[] vertices,
        int[] triangles)
    {
        return new CollisionMeshAuthoringEntry
        {
            SourcePath = sourcePath,
            LayerMask = CollisionLayers.CharacterMovement,
            Vertices = CollisionAuthoringEncoding.EncodeFloats(
                vertices.SelectMany(vertex => new[] { vertex.X, vertex.Y, vertex.Z }).ToArray()),
            Triangles = CollisionAuthoringEncoding.EncodeInts(triangles)
        };
    }

    // Heights are a function of the world X and Z of each sample.
    private static CollisionHeightfieldAuthoringEntry Heightfield(
        string sourcePath,
        float originX,
        float originZ,
        float spacing,
        int samplesX,
        int samplesZ,
        Func<float, float, float> height,
        Func<int, bool>? isHole = null)
    {
        var heights = new float[samplesX * samplesZ];
        for (var z = 0; z < samplesZ; z++)
        {
            for (var x = 0; x < samplesX; x++)
            {
                heights[(z * samplesX) + x] = height(originX + (x * spacing), originZ + (z * spacing));
            }
        }

        var holes = isHole == null
            ? []
            : Enumerable.Range(0, (samplesX - 1) * (samplesZ - 1))
                .Select(cell => isHole(cell) ? (byte)1 : (byte)0)
                .ToArray();
        return new CollisionHeightfieldAuthoringEntry
        {
            SourcePath = sourcePath,
            LayerMask = CollisionLayers.CharacterMovement,
            OriginX = originX,
            OriginZ = originZ,
            SpacingX = spacing,
            SpacingZ = spacing,
            SamplesX = samplesX,
            SamplesZ = samplesZ,
            Heights = CollisionAuthoringEncoding.EncodeFloats(heights),
            Holes = CollisionAuthoringEncoding.EncodeBytes(holes)
        };
    }
}
