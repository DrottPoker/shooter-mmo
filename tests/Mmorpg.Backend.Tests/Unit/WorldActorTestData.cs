using Mmorpg.Tools.WorldActorCompiler;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;

namespace Mmorpg.Backend.Tests.Unit;

internal static class WorldActorTestData
{
    public static string ActorsDirectory => ContentLayout.ActorsDirectory(RepositoryPaths.Content);

    public static WorldActorCatalogAuthoringDocument LoadActors()
    {
        return WorldActorContent.LoadCatalog(ActorsDirectory);
    }

    public static WorldActorSpawnAuthoringDocument LoadSpawns(
        string worldId = "development-world-1")
    {
        return WorldActorContent.LoadSpawns(RepositoryPaths.Content, worldId);
    }

    public static WorldActorRuntimeDocument Compile(string worldId = "development-world-1")
    {
        return WorldActorCompiler.Compile(LoadActors(), LoadSpawns(worldId));
    }

    public static string ActorCatalogJson => File.ReadAllText(
        ContentLayout.CatalogPath(ActorsDirectory));

    public static string SpawnAuthoringJson => File.ReadAllText(
        ContentLayout.ActorSpawnsPath(RepositoryPaths.Content, "development-world-1"));

    public static string RuntimeJson => RuntimeJsonFor("development-world-1");

    public static string RuntimeJsonFor(string worldId)
    {
        return File.ReadAllText(
            ContentLayout.GeneratedWorldActorsPath(RepositoryPaths.Generated, worldId));
    }
}
