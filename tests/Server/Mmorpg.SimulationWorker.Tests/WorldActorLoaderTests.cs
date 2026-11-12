using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing;
using Mmorpg.Testing.Content;
using Mmorpg.Tools.WorldActorCompiler;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class WorldActorLoaderTests
{
    public static TheoryData<string> CheckedInWorldIds => new(WorldActorTestData.CheckedInWorldIds());

    [Fact]
    public void RuntimeStructuralTamperingFailsStartupValidation()
    {
        var runtime = WorldActorTestData.Compile();
        runtime.Catalog.Actors[0].Level++;

        Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.ValidateRuntime(runtime));
        Assert.Throws<InvalidDataException>(
            () => WorldActorLoader.Validate(
                runtime,
                runtime.Spawns.WorldId));
    }

    [Theory]
    [MemberData(nameof(CheckedInWorldIds))]
    public void TheCheckedInCatalogAndSpawnsLoadTogether(string worldId)
    {
        var runtime = WorldActorLoader.Load(
            ContentLayout.GeneratedActorCatalogPath(RepositoryPaths.Generated),
            ContentLayout.GeneratedWorldActorSpawnsPath(RepositoryPaths.Generated, worldId),
            worldId);

        Assert.Equal(WorldActorTestData.CompileCatalog().Revision, runtime.Catalog.Revision);
        Assert.Equal(worldId, runtime.Spawns.WorldId);
    }

    // The two files are compiled together but deployed apart, so a spawn file compiled against
    // another catalog must stop the worker.
    [Fact]
    public void ALoaderRefusesSpawnsOfAnotherCatalog()
    {
        var runtime = WorldActorTestData.Compile();
        runtime.Spawns.CatalogId = "other";
        WorldActorCompiler.StampSpawns(runtime.Spawns);

        var exception = Assert.Throws<InvalidDataException>(() => LoadFromFiles(runtime));

        Assert.Contains(
            "runtime spawn catalogId 'other' does not match actor catalog 'core'",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ALoaderRefusesSpawnsOfAnActorTheCatalogLacks()
    {
        var runtime = WorldActorTestData.Compile();
        var source = runtime.Spawns.SpawnSources.First(value => value.ActorId == "mob.feral_wolf");
        source.ActorId = "mob.missing";
        foreach (var instance in runtime.Spawns.SpawnInstances.Where(value => value.SpawnDefinitionId == source.Id))
        {
            instance.ActorId = "mob.missing";
        }

        WorldActorCompiler.StampSpawns(runtime.Spawns);
        WorldActorCompiler.ValidateRuntime(runtime.Spawns);

        var exception = Assert.Throws<InvalidDataException>(() => LoadFromFiles(runtime));

        Assert.Contains("references missing id 'mob.missing'", exception.Message, StringComparison.Ordinal);
    }

    private static WorldActorRuntime LoadFromFiles(WorldActorRuntime runtime)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Mmorpg.WorldActorLoaderTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var catalogPath = Path.Combine(directory, ContentLayout.ActorCatalogFileName);
            var spawnsPath = Path.Combine(directory, ContentLayout.WorldActorSpawnsFileName);
            File.WriteAllText(catalogPath, WorldActorJson.SerializeCatalogRuntime(runtime.Catalog));
            File.WriteAllText(spawnsPath, WorldActorJson.SerializeSpawnRuntime(runtime.Spawns));
            return WorldActorLoader.Load(catalogPath, spawnsPath, runtime.Spawns.WorldId);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
