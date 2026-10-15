using System.Text.Json;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SharedTests;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Backend.Tests.Unit;

public sealed class SharedSimulationHardeningTests
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void SharedMovementMatchesThePinnedRevisionAndFingerprint()
    {
        // A failure here means shared movement or collision behavior changed. If the change
        // is intended, bump GameSimulationCompatibility.Revision and pin the new fingerprint
        // in MovementDeterminismScenario, which the Unity EditMode tests also check.
        Assert.Equal(
            MovementDeterminismScenario.ExpectedSimulationRevision,
            GameSimulationCompatibility.Revision);
        Assert.Equal(
            MovementDeterminismScenario.ExpectedFingerprint,
            MovementDeterminismScenario.ComputeFingerprint());
    }

    [Fact]
    public void DeterminismScenarioExercisesEveryCollisionCase()
    {
        var states = MovementDeterminismScenario.ComputeTrace().States;

        // Blocked by the wall face at x = 6 minus the capsule radius.
        Assert.True(states.Count(state =>
            state.PositionX is > 5.6f and < 5.7f && Math.Abs(state.PositionZ) < 3.5f) >= 10);
        // Climbed the rotated ramp to its high edge and dropped off it.
        Assert.Contains(states, state => state.PositionY > 2f);
        // Stopped against a face of the rotated pillar.
        Assert.Contains(states, state => DistanceXz(state, 2f, -8f) < 0.9f);
        // Stepped up onto the step and jumped from it.
        Assert.True(states.Count(state =>
            Math.Abs(state.PositionY - 0.3f) < 0.005f && state.PositionX is > -8f and < -4f) >= 10);
        Assert.True(states.Count(state => !state.IsGrounded) >= 50);
    }

    [Fact]
    public void DiagonalSubstepCannotTunnelThroughAThinRotatedWall()
    {
        // The largest substep the settings allow equals the radius. A wall turned by 45
        // degrees makes a diagonal move cross it along its normal.
        var settings = new CharacterCollisionSettings(0.35f, 2f, 0.35f, 45f, 0.4f, 0.35f, 6);
        var world = CompileWorld(
            16f,
            Box("Hardening/ThinWall", 0f, 1f, 0f, 0.01f, 1f, 5f, 0f, 0.38268343f, 0f, 0.9238795f));
        var normalX = 0.70710677f;
        var normalZ = -0.70710677f;
        var start = new SimulationVector3(-0.36f * normalX, 0.5f, -0.36f * normalZ);
        var displacement = new SimulationVector3(0.98f * normalX, 0f, 0.98f * normalZ);

        var result = KinematicCharacterMotor.Move(
            world,
            settings,
            start,
            displacement,
            new SimulationVector3(0f, 0f, 0f),
            false);

        var signedDistance = (result.Position.X * normalX) + (result.Position.Z * normalZ);
        Assert.True(signedDistance < 0f, $"The capsule tunneled to {signedDistance}.");
    }

    [Fact]
    public void SettingsBudgetCoversSprintAndFallTogether()
    {
        // Sprint or fall alone fits 256 substeps of 0.1, but sprinting while falling
        // does not, and the motor would reject that move at runtime.
        var exception = Assert.Throws<ArgumentException>(() => new MovementSimulationSettings(
            1,
            5f,
            20f,
            720f,
            -24f,
            20f,
            7f,
            -2f,
            0f,
            -14f,
            14f,
            -14f,
            14f,
            CharacterCollisionSettings.Default));

        Assert.Contains("collision substep budget", exception.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 5_000)]
    public async Task UnboundedQueriesFinishAndStillReturnLoadedBoxes()
    {
        var world = CompileWorld(32f, Box("Hardening/Ground", 0f, -0.25f, 0f, 15f, 0.25f, 15f));
        var dynamicWorld = new DynamicCollisionWorld(32f);
        dynamicWorld.Upsert(new CollisionBox(
            7,
            CollisionLayers.CharacterMovement,
            new SimulationVector3(2f, 1f, 2f),
            new SimulationVector3(0.5f, 1f, 0.5f),
            new SimulationQuaternion(0f, 0f, 0f, 1f)));
        var everywhere = new CollisionAabb(
            new SimulationVector3(-1e30f, -1e30f, -1e30f),
            new SimulationVector3(1e30f, 1e30f, 1e30f));
        var staticResults = new CollisionQueryBuffer();
        var dynamicResults = new CollisionQueryBuffer();

        await Task.Run(
            () =>
            {
                world.QueryBoxes(everywhere, CollisionLayers.All, staticResults);
                dynamicWorld.QueryBoxes(everywhere, CollisionLayers.All, dynamicResults);
            },
            TestContext.Current.CancellationToken);

        Assert.Single(staticResults.Boxes);
        Assert.Single(dynamicResults.Boxes);
    }

    [Fact]
    public void CollisionDataOutsideTheSupportedWorldLimitsIsRejected()
    {
        Assert.Throws<ArgumentException>(() => CompileWorld(
            32f,
            Box("Hardening/Far", 2_000_000f, 0f, 0f, 1f, 1f, 1f)));
        Assert.Throws<InvalidDataException>(() => CompileWorld(
            1f,
            Box("Hardening/Huge", 0f, 0f, 0f, 200f, 1f, 200f)));
        Assert.Throws<InvalidDataException>(() => CompileWorld(
            0.5f,
            Box("Hardening/Small", 0f, 0f, 0f, 1f, 1f, 1f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DynamicCollisionWorld(8192f));
        Assert.Throws<ArgumentException>(() => new DynamicCollisionWorld(1f).Upsert(
            new CollisionBox(
                9,
                CollisionLayers.CharacterMovement,
                new SimulationVector3(0f, 0f, 0f),
                new SimulationVector3(500f, 1f, 500f),
                new SimulationQuaternion(0f, 0f, 0f, 1f))));
    }

    [Theory]
    [InlineData("development-world-1")]
    [InlineData("development-world-2")]
    public void CommittedCollisionManifestsMatchTheirRecomputedRevision(string worldId)
    {
        var manifest = LoadCommittedManifest(worldId);

        Assert.Equal(manifest.Revision, CollisionWorldCompiler.ComputeManifestRevision(manifest));

        // A manifest whose chunk list changed without a new revision no longer matches.
        manifest.Chunks[0].Sha256 = new string('0', 64);
        Assert.NotEqual(manifest.Revision, CollisionWorldCompiler.ComputeManifestRevision(manifest));
    }

    [Fact]
    public void ItemCatalogLimitsFitTheRealtimeProtocolLimits()
    {
        Assert.True(ItemCatalogFormat.MaximumIdentifierLength <= RealtimeProtocolLimits.MaximumDefinitionIdLength);
        Assert.True(ItemCatalogFormat.MaximumTagLength <= RealtimeProtocolLimits.MaximumSlotTagLength);
        Assert.True(ItemCatalogFormat.MaximumAcceptedTagsPerSlot <= RealtimeProtocolLimits.MaximumSlotTags);
        Assert.True(
            ItemCatalogFormat.MaximumEquipmentSlotIdLength
            <= RealtimeProtocolLimits.MaximumEquipmentSlotIdLength);

        // The committed catalog compiles under these limits.
        Assert.NotEmpty(ItemCatalogTestData.Compile().Definitions);
    }

    [Fact]
    public void ItemCatalogRejectsTagsAndEquipmentSlotIdsLongerThanTheProtocolAllows()
    {
        var longId = "a" + new string('b', ItemCatalogFormat.MaximumTagLength);
        var withLongTag = ItemCatalogTestData.LoadAuthoring();
        withLongTag.Tags = withLongTag.Tags
            .Append(new ItemCatalogIdentityEntry { Id = longId, DisplayName = "Long Tag" })
            .ToArray();
        var withLongSlot = ItemCatalogTestData.LoadAuthoring();
        withLongSlot.EquipmentSlots = withLongSlot.EquipmentSlots
            .Append(new ItemCatalogIdentityEntry { Id = longId, DisplayName = "Long Slot" })
            .ToArray();

        var tagError = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(withLongTag));
        var slotError = Assert.Throws<ItemCatalogValidationException>(
            () => ItemCatalogCompiler.Compile(withLongSlot));

        Assert.Contains("tags[", tagError.Message, StringComparison.Ordinal);
        Assert.Contains("equipmentSlots[", slotError.Message, StringComparison.Ordinal);
    }

    private static float DistanceXz(PlayerMovementState state, float x, float z)
    {
        var deltaX = state.PositionX - x;
        var deltaZ = state.PositionZ - z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private static CollisionWorldManifest LoadCommittedManifest(string worldId)
    {
        var path = Path.Combine(
            RepositoryPaths.Worlds,
            worldId,
            "Runtime",
            "Resources",
            "WorldCollision",
            worldId,
            "manifest.json");
        return JsonSerializer.Deserialize<CollisionWorldManifest>(
                File.ReadAllText(path),
                ManifestJsonOptions)
            ?? throw new InvalidDataException("The committed collision manifest is empty.");
    }

    private static ChunkedStaticCollisionWorld CompileWorld(
        float chunkSize,
        params CollisionBoxAuthoringEntry[] boxes)
    {
        var bake = CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "hardening-world",
            ChunkSize = chunkSize,
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
