using ShooterMmo.Tools.WorldActorCompiler;
using ShooterMmo.WorldData.Actors;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class WorldActorCompilerTests
{
    [Fact]
    public void CheckedInAuthoringMatchesDeterministicRuntime()
    {
        var runtime = WorldActorTestData.Compile();

        Assert.Equal(WorldActorDataFormat.Version, runtime.FormatVersion);
        Assert.Equal("core", runtime.CatalogId);
        Assert.Equal("development-world-1", runtime.WorldId);
        Assert.Equal(64, runtime.Revision.Length);
        Assert.Equal(4, runtime.Actors.Length);
        Assert.Equal(5, runtime.SpawnInstances.Length);
        Assert.Equal(
            WorldActorJson.SerializeRuntime(runtime),
            NormalizeLineEndings(WorldActorTestData.RuntimeJson));
    }

    [Fact]
    public void DevelopmentWorldTwoAuthoringMatchesDeterministicRuntime()
    {
        const string worldId = "development-world-2";
        var runtime = WorldActorTestData.Compile(worldId);

        Assert.Equal(worldId, runtime.WorldId);
        Assert.Equal(4, runtime.Actors.Length);
        Assert.Equal(7, runtime.SpawnInstances.Length);
        Assert.Equal(
            WorldActorJson.SerializeRuntime(runtime),
            NormalizeLineEndings(WorldActorTestData.RuntimeJsonFor(worldId)));
    }

    [Fact]
    public void EquivalentAuthoringOrderProducesSameRevisionAndOutput()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        var expected = WorldActorCompiler.Compile(actors, spawns);

        Array.Reverse(actors.Factions);
        Array.Reverse(actors.PresentationArchetypes);
        Array.Reverse(actors.ActivityProfiles);
        Array.Reverse(actors.RespawnProfiles);
        Array.Reverse(actors.Actors);
        foreach (var actor in actors.Actors)
        {
            Array.Reverse(actor.Capabilities);
            Array.Reverse(actor.Tags);
        }

        Array.Reverse(spawns.SpawnPoints);
        Array.Reverse(spawns.SpawnGroups);
        Array.Reverse(spawns.SpawnAreas);
        Array.Reverse(spawns.PatrolPaths);
        var reordered = WorldActorCompiler.Compile(actors, spawns);

        Assert.Equal(expected.Revision, reordered.Revision);
        Assert.Equal(
            WorldActorJson.SerializeRuntime(expected),
            WorldActorJson.SerializeRuntime(reordered));
    }

    [Fact]
    public void InvalidTagsAndPopulationLimitsFailValidation()
    {
        var actors = WorldActorTestData.LoadActors();
        actors.Actors[0].Tags = ["wild", "wild"];
        actors.RespawnProfiles[0].PopulationLimit = 0;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.Contains(exception.Errors, value => value.Contains("tags", StringComparison.Ordinal));
        Assert.Contains(
            exception.Errors,
            value => value.Contains("populationLimit", StringComparison.Ordinal));
    }

    [Fact]
    public void NpcComposesCapabilitiesWithoutActorSubclass()
    {
        var runtime = WorldActorTestData.Compile();
        var actor = runtime.Actors.Single(value => value.Id == "npc.city_services");

        Assert.Equal(WorldActorKindIds.Npc, actor.Kind);
        Assert.Equal(8, actor.Capabilities.Length);
        Assert.Contains(actor.Capabilities, value => value.Kind == WorldActorCapabilityKindIds.Dialogue);
        Assert.Contains(actor.Capabilities, value => value.Kind == WorldActorCapabilityKindIds.Vendor);
        Assert.Contains(actor.Capabilities, value => value.Kind == WorldActorCapabilityKindIds.QuestOffer);
        Assert.Contains(actor.Capabilities, value => value.Kind == WorldActorCapabilityKindIds.Crafting);
        Assert.All(runtime.Actors, value => Assert.IsType<WorldActorDefinition>(value));
    }

    [Fact]
    public void MobCorpseLifetimeAndPersistenceAreContentControlledPerDefinition()
    {
        var runtime = WorldActorTestData.Compile();
        var normalMob = runtime.Actors.Single(value => value.Id == "mob.feral_wolf");
        var bossMob = runtime.Actors.Single(value => value.Id == "mob.feral_alpha");

        Assert.Equal(WorldActorCorpsePersistenceModeIds.Live, normalMob.CorpsePersistenceMode);
        Assert.Equal(120f, normalMob.CorpseLifetimeSeconds);
        Assert.Equal(WorldActorCorpsePersistenceModeIds.Durable, bossMob.CorpsePersistenceMode);
        Assert.Equal(600f, bossMob.CorpseLifetimeSeconds);
    }

    [Fact]
    public void InvalidMobCorpseSettingsAndNpcCorpseSettingsFailValidation()
    {
        var actors = WorldActorTestData.LoadActors();
        var mob = actors.Actors.Single(value => value.Id == "mob.feral_wolf");
        var npc = actors.Actors.Single(value => value.Id == "npc.city_guard");
        mob.CorpsePersistenceMode = "unsupported";
        mob.CorpseLifetimeSeconds = 0f;
        npc.CorpsePersistenceMode = WorldActorCorpsePersistenceModeIds.Live;
        npc.CorpseLifetimeSeconds = 120f;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.Contains(
            exception.Errors,
            value => value.Contains("corpsePersistenceMode", StringComparison.Ordinal));
        Assert.Contains(
            exception.Errors,
            value => value.Contains("corpseLifetimeSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateContentAndSpawnIdsFailValidation()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        actors.Actors[1].Id = actors.Actors[0].Id;
        spawns.SpawnGroups[0].Id = spawns.SpawnPoints[0].Id;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, spawns));

        Assert.Contains(exception.Errors, value => value.Contains("duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingReferencesFailValidation()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        actors.Actors[0].FactionId = "missing.faction";
        spawns.SpawnPoints[0].ActorId = "missing.actor";
        spawns.SpawnAreas[0].PatrolPathId = "missing.path";

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, spawns));

        Assert.True(exception.Errors.Count(value => value.Contains("missing id", StringComparison.Ordinal)) >= 3);
    }

    [Fact]
    public void InvalidTransformAndBoundsFailValidation()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        actors.Actors[0].InteractionBounds.SizeX = 0f;
        spawns.SpawnPoints[0].X = float.NaN;
        spawns.SpawnAreas[0].MaximumZ = spawns.SpawnAreas[0].MinimumZ;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, spawns));

        Assert.Contains(exception.Errors, value => value.Contains("sizeX", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains("spawnPoints[0].x", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains("positive X and Z size", StringComparison.Ordinal));
    }

    [Fact]
    public void WorldCoordinatesAndInteractionBoundsAreExplicitlyBounded()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        actors.Actors[0].InteractionBounds.SizeX = 1_000_001f;
        spawns.SpawnPoints[0].X = 1_000_001f;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, spawns));

        Assert.Contains(
            exception.Errors,
            value => value.Contains("supported bound", StringComparison.Ordinal));
        Assert.Contains(
            exception.Errors,
            value => value.Contains("supported World bounds", StringComparison.Ordinal));
    }

    [Fact]
    public void ExcessiveSpawnDimensionsFailBeforeInstanceExpansion()
    {
        var spawns = WorldActorTestData.LoadSpawns();
        spawns.SpawnGroups[0].Rows = int.MaxValue;
        spawns.SpawnGroups[0].Columns = int.MaxValue;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(WorldActorTestData.LoadActors(), spawns));

        Assert.Contains(
            exception.Errors,
            value => value.Contains("instances", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unsupported", "invulnerable")]
    [InlineData("npc", "damageable")]
    public void UnsupportedKindOrDamageableNpcFailsValidation(string kind, string damagePolicy)
    {
        var actors = WorldActorTestData.LoadActors();
        var actor = actors.Actors.Single(value => value.Id == "npc.city_guard");
        actor.Kind = kind;
        actor.DamagePolicy = damagePolicy;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.NotEmpty(exception.Errors);
    }

    [Fact]
    public void UnknownJsonMemberAndDuplicatePropertyFailStrictParsing()
    {
        var unknown = WorldActorTestData.ActorAuthoringJson.Replace(
            "\"catalogId\": \"core\",",
            "\"catalogId\": \"core\",\n  \"unknown\": true,",
            StringComparison.Ordinal);
        var duplicate = WorldActorTestData.SpawnAuthoringJson.Replace(
            "\"worldId\": \"development-world-1\",",
            "\"worldId\": \"development-world-1\",\n  \"worldId\": \"duplicate\",",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => WorldActorJson.DeserializeActors(unknown));
        Assert.Throws<InvalidDataException>(() => WorldActorJson.DeserializeSpawns(duplicate));
    }

    [Fact]
    public void ClosestPointDistanceUsesServerOwnedBounds()
    {
        var distanceSquared = WorldInteractionRules.DistanceSquaredToBounds(
            0f,
            0f,
            0f,
            3f,
            1f,
            0f,
            2f,
            2f,
            2f);

        Assert.Equal(4f, distanceSquared, 4);
    }

    [Fact]
    public void RuntimeStructuralTamperingFailsStartupValidation()
    {
        var runtime = WorldActorTestData.Compile();
        runtime.Actors[0].DisplayName = "Tampered Actor";

        Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.ValidateRuntime(runtime));
        Assert.Throws<InvalidDataException>(
            () => SimulationWorker.WorldActors.WorldActorLoader.Validate(
                runtime,
                runtime.WorldId));
    }

    [Fact]
    public void RuntimeInstanceMustMatchItsDeterministicSpawnSource()
    {
        var runtime = WorldActorTestData.Compile();
        runtime.SpawnInstances[0].X += 0.5f;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.ValidateRuntime(runtime));

        Assert.Contains(
            exception.Errors,
            value => value.Contains(
                "deterministic source placement",
                StringComparison.Ordinal));
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }
}
