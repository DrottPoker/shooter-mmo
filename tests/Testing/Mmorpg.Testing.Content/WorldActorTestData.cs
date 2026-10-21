using Mmorpg.Testing;
using Mmorpg.Tools.WorldActorCompiler;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;

namespace Mmorpg.Testing.Content;

// Simulation and compiler rule tests use the test-owned spawn layout in Fixtures/WorldActors,
// so the development Worlds can change without breaking them. Content tests name a World
// and read its checked-in spawns and generated runtime instead.
public static class WorldActorTestData
{
    // The fixture runs under the identity the realtime test harness joins.
    public const string FixtureWorldId = "development-world-1";

    public static string ActorsDirectory => ContentLayout.ActorsDirectory(RepositoryPaths.Content);

    public static WorldActorCatalogAuthoringDocument LoadActors()
    {
        return WorldActorContent.LoadCatalog(ActorsDirectory);
    }

    public static WorldActorSpawnAuthoringDocument LoadSpawns()
    {
        return WorldActorJson.DeserializeSpawns(SpawnAuthoringJson);
    }

    public static WorldActorRuntimeDocument Compile()
    {
        return WorldActorCompiler.Compile(LoadActors(), LoadSpawns());
    }

    public static string ActorCatalogJson => File.ReadAllText(
        ContentLayout.CatalogPath(ActorsDirectory));

    public static string SpawnAuthoringJson => File.ReadAllText(
        RepositoryPaths.WorldActorFixtureSpawnsPath);

    public static WorldActorSpawnAuthoringDocument LoadWorldSpawns(string worldId)
    {
        return WorldActorContent.LoadSpawns(RepositoryPaths.Content, worldId);
    }

    public static WorldActorRuntimeDocument CompileWorld(string worldId)
    {
        return WorldActorCompiler.Compile(LoadActors(), LoadWorldSpawns(worldId));
    }

    public static string RuntimeJsonFor(string worldId)
    {
        return File.ReadAllText(
            ContentLayout.GeneratedWorldActorsPath(RepositoryPaths.Generated, worldId));
    }

    // Every checked-in World, for content tests that must cover all of them.
    public static IReadOnlyList<string> CheckedInWorldIds()
    {
        return ContentLayout.ListWorldIds(RepositoryPaths.Worlds);
    }
}
