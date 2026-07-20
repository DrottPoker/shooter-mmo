using ShooterMmo.GameSimulation;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class WorldCollisionTests
{
    private static readonly MovementSimulationSettings MovementSettings = new(
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
        14f,
        CharacterCollisionSettings.Default);

    [Fact]
    public void CompilerProducesDeterministicVersionedChunks()
    {
        var authoring = CreateAuthoring(Ground());

        var first = CollisionWorldCompiler.Compile(authoring);
        var second = CollisionWorldCompiler.Compile(authoring);

        Assert.Equal(4, first.Chunks.Count);
        Assert.Equal(first.Manifest.Revision, second.Manifest.Revision);
        Assert.All(first.Chunks, chunk =>
        {
            Assert.Equal(chunk.ManifestEntry.Sha256, CollisionWorldCompiler.ComputeSha256Hex(chunk.Data));
            var decoded = CollisionChunkCodec.Decode(chunk.Data);
            Assert.Contains(decoded.Boxes, box =>
                box.StableId == CollisionWorldCompiler.StableIdFromPath("Test/Ground"));
        });
    }

    [Fact]
    public void CompilerCanonicalizesNegativeZeroAcrossRuntimes()
    {
        var negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        var positive = CreateAuthoring(Box(
            "Test/CanonicalZero",
            0f,
            0f,
            0f,
            1f,
            1f,
            1f));
        var negative = CreateAuthoring(Box(
            "Test/CanonicalZero",
            negativeZero,
            0f,
            negativeZero,
            1f,
            1f,
            1f,
            negativeZero,
            negativeZero,
            negativeZero));

        var positiveBake = CollisionWorldCompiler.Compile(positive);
        var negativeBake = CollisionWorldCompiler.Compile(negative);

        Assert.Equal(positiveBake.Manifest.Revision, negativeBake.Manifest.Revision);
        Assert.Equal(positiveBake.Chunks.Count, negativeBake.Chunks.Count);
        for (var index = 0; index < positiveBake.Chunks.Count; index++)
        {
            Assert.Equal(positiveBake.Chunks[index].Data, negativeBake.Chunks[index].Data);
        }
    }

    [Fact]
    public void NearUnitQuaternionPreservesAuthoredBitsAcrossRuntimes()
    {
        var authored = new SimulationQuaternion(
            0.005459484f,
            0.15633918f,
            0.034469824f,
            0.9870867f);

        var normalized = authored.Normalized();

        Assert.Equal(
            BitConverter.SingleToInt32Bits(authored.X),
            BitConverter.SingleToInt32Bits(normalized.X));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(authored.Y),
            BitConverter.SingleToInt32Bits(normalized.Y));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(authored.Z),
            BitConverter.SingleToInt32Bits(normalized.Z));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(authored.W),
            BitConverter.SingleToInt32Bits(normalized.W));
    }

    [Fact]
    public void AuthoritativeCapsuleStopsAtWallWithoutTunneling()
    {
        var world = CompileWorld(
            Ground(),
            Box("Test/Wall", 0f, 1.5f, 2f, 4f, 1.5f, 0.25f));
        var state = PlayerMovementSimulation.CreateInitialState(
            MovementSettings,
            world,
            0f,
            0f,
            0f,
            0f);

        for (var tick = 0; tick < 60; tick++)
        {
            state = PlayerMovementSimulation.Step(
                state,
                Input((uint)(tick + 1), 0f, 1f, PlayerMovementButtons.Sprint),
                MovementSettings,
                PlayerCarryState.Default,
                world);
        }

        Assert.InRange(state.PositionZ, 1.39f, 1.401f);
        Assert.True(state.IsGrounded);
    }

    [Fact]
    public void AuthoritativeCapsuleClimbsWalkableRamp()
    {
        var world = CompileWorld(
            Ground(),
            Box(
                "Test/Ramp",
                0f,
                0.4f,
                -7f,
                2.5f,
                0.25f,
                4f,
                0.10452846f,
                0f,
                0f,
                0.9945219f));
        var state = PlayerMovementSimulation.CreateInitialState(
            MovementSettings,
            world,
            0f,
            0f,
            -2f,
            180f);

        for (var tick = 0; tick < 30; tick++)
        {
            state = PlayerMovementSimulation.Step(
                state,
                Input((uint)(tick + 1), 0f, 1f, PlayerMovementButtons.None, 180f),
                MovementSettings,
                PlayerCarryState.Default,
                world);
        }

        Assert.True(state.PositionZ < -5f);
        Assert.True(state.PositionY > 0.2f);
        Assert.True(state.IsGrounded);
    }

    [Fact]
    public void WalkableRampHoldsIdleCharacterInPlace()
    {
        var world = CompileWorld(
            Ground(),
            Box(
                "Test/Ramp",
                0f,
                0.4f,
                -7f,
                2.5f,
                0.25f,
                4f,
                0.10452846f,
                0f,
                0f,
                0.9945219f));
        var state = PlayerMovementSimulation.CreateInitialState(
            MovementSettings,
            world,
            0f,
            0f,
            -2f,
            180f);

        for (var tick = 0; tick < 18; tick++)
        {
            state = PlayerMovementSimulation.Step(
                state,
                Input((uint)(tick + 1), 0f, 1f, PlayerMovementButtons.None, 180f),
                MovementSettings,
                PlayerCarryState.Default,
                world);
        }

        var restingX = state.PositionX;
        var restingY = state.PositionY;
        var restingZ = state.PositionZ;
        for (var tick = 0; tick < 60; tick++)
        {
            state = PlayerMovementSimulation.Step(
                state,
                Input((uint)(tick + 19), 0f, 0f, PlayerMovementButtons.None, 180f),
                MovementSettings,
                PlayerCarryState.Default,
                world);
        }

        Assert.True(state.IsGrounded);
        Assert.InRange(Math.Abs(state.PositionX - restingX), 0f, 0.0001f);
        Assert.InRange(Math.Abs(state.PositionY - restingY), 0f, 0.0001f);
        Assert.InRange(Math.Abs(state.PositionZ - restingZ), 0f, 0.0001f);

        var queryBuffer = new CollisionQueryBuffer();
        Assert.True(KinematicCharacterMotor.TryFindWalkableGroundSurface(
            world,
            MovementSettings.CharacterCollision,
            new SimulationVector3(state.PositionX, state.PositionY, state.PositionZ),
            0f,
            MovementSettings.CharacterCollision.GroundSnapDistance
                + MovementSettings.CharacterCollision.Radius,
            queryBuffer,
            out var surfaceHeight,
            out var groundNormal));
        var expectedSupportHeight = KinematicCharacterMotor.CalculateCapsuleSupportHeight(
            surfaceHeight,
            groundNormal,
            MovementSettings.CharacterCollision.Radius);
        Assert.InRange(Math.Abs(state.PositionY - expectedSupportHeight), 0f, 0.0001f);
        Assert.True(state.PositionY > surfaceHeight);
    }

    [Fact]
    public void RampAboveSlopeLimitIsNotWalkableGround()
    {
        var world = CompileWorld(Box(
            "Test/SteepRamp",
            0f,
            0.4f,
            -7f,
            2.5f,
            0.25f,
            4f,
            0.4617486f,
            0f,
            0f,
            0.8870108f));
        var queryBuffer = new CollisionQueryBuffer();

        var found = KinematicCharacterMotor.TryFindWalkableGroundSurface(
            world,
            MovementSettings.CharacterCollision,
            new SimulationVector3(0f, 2f, -7f),
            0f,
            4f,
            queryBuffer,
            out _,
            out _);

        Assert.False(found);
    }

    [Fact]
    public void AuthoritativeCapsuleClimbsConfiguredSteps()
    {
        var world = CompileWorld(
            Ground(),
            Box("Test/Step01", -8f, 0.15f, -7f, 1f, 0.15f, 1f),
            Box("Test/Step02", -8f, 0.3f, -5.5f, 1f, 0.3f, 1f),
            Box("Test/Step03", -8f, 0.45f, -4f, 1f, 0.45f, 1f));
        var state = PlayerMovementSimulation.CreateInitialState(
            MovementSettings,
            world,
            -8f,
            0f,
            -9f,
            0f);

        for (var tick = 0; tick < 35; tick++)
        {
            state = PlayerMovementSimulation.Step(
                state,
                Input((uint)(tick + 1), 0f, 1f, PlayerMovementButtons.None),
                MovementSettings,
                PlayerCarryState.Default,
                world);
        }

        Assert.True(
            state.PositionZ > -5f,
            $"Expected the character to climb the steps, actual position was ({state.PositionX}, {state.PositionY}, {state.PositionZ}).");
        Assert.InRange(state.PositionY, 0.89f, 0.901f);
    }

    [Fact]
    public void DynamicCollisionWorldUpdatesSpatialMemberships()
    {
        var world = new DynamicCollisionWorld(8f);
        var box = Box("Dynamic/Door", 1f, 1f, 1f, 0.5f, 1f, 0.5f).ToCollisionBox();
        world.Upsert(box);
        var buffer = new CollisionQueryBuffer();
        world.QueryBoxes(
            new CollisionAabb(
                new SimulationVector3(0f, 0f, 0f),
                new SimulationVector3(2f, 2f, 2f)),
            CollisionLayers.CharacterMovement,
            buffer);
        Assert.Single(buffer.Boxes);

        world.Upsert(Box("Dynamic/Door", 20f, 1f, 20f, 0.5f, 1f, 0.5f).ToCollisionBox());
        buffer.Clear();
        world.QueryBoxes(
            new CollisionAabb(
                new SimulationVector3(0f, 0f, 0f),
                new SimulationVector3(2f, 2f, 2f)),
            CollisionLayers.CharacterMovement,
            buffer);
        Assert.Empty(buffer.Boxes);

        Assert.True(world.Remove(box.StableId));
        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void StaticCollisionChunksCanBeLoadedAndUnloadedForStreaming()
    {
        var bake = CollisionWorldCompiler.Compile(CreateAuthoring(Ground()));
        var chunks = bake.Chunks
            .Select(chunk => CollisionChunkCodec.Decode(chunk.Data))
            .ToArray();
        var world = new ChunkedStaticCollisionWorld(
            bake.Manifest.WorldId,
            bake.Manifest.Revision,
            bake.Manifest.ChunkSize,
            chunks);
        var positiveChunk = chunks.Single(chunk => chunk.X == 0 && chunk.Z == 0);
        var queryBounds = new CollisionAabb(
            new SimulationVector3(1f, -1f, 1f),
            new SimulationVector3(2f, 1f, 2f));
        var buffer = new CollisionQueryBuffer();

        Assert.True(world.UnloadChunk(0, 0));
        world.QueryBoxes(queryBounds, CollisionLayers.CharacterMovement, buffer);
        Assert.Empty(buffer.Boxes);

        Assert.True(world.TryLoadChunk(positiveChunk));
        buffer.Clear();
        world.QueryBoxes(queryBounds, CollisionLayers.CharacterMovement, buffer);
        Assert.Single(buffer.Boxes);
    }

    private static PlayerMovementInput Input(
        uint sequence,
        float moveX,
        float moveY,
        PlayerMovementButtons buttons,
        float cameraYaw = 0f)
    {
        return new PlayerMovementInput(sequence, sequence, moveX, moveY, cameraYaw, buttons);
    }

    private static ChunkedStaticCollisionWorld CompileWorld(
        params CollisionBoxAuthoringEntry[] boxes)
    {
        var bake = CollisionWorldCompiler.Compile(CreateAuthoring(boxes));
        return new ChunkedStaticCollisionWorld(
            bake.Manifest.WorldId,
            bake.Manifest.Revision,
            bake.Manifest.ChunkSize,
            bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
    }

    private static CollisionWorldAuthoringDocument CreateAuthoring(
        params CollisionBoxAuthoringEntry[] boxes)
    {
        return new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "development-world-1",
            ChunkSize = 32f,
            Boxes = boxes
        };
    }

    private static CollisionBoxAuthoringEntry Ground()
    {
        return Box("Test/Ground", 0f, -0.25f, 0f, 15f, 0.25f, 15f);
    }

    private static CollisionBoxAuthoringEntry Box(
        string sourcePath,
        float centerX,
        float centerY,
        float centerZ,
        float halfExtentX,
        float halfExtentY,
        float halfExtentZ,
        float rotationX = 0f,
        float rotationY = 0f,
        float rotationZ = 0f,
        float rotationW = 1f)
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
            RotationX = rotationX,
            RotationY = rotationY,
            RotationZ = rotationZ,
            RotationW = rotationW
        };
    }
}
