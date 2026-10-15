using Mmorpg.Tools.WorldActorCompiler;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.Backend.Tests.Unit;

internal static class WorldActorTestData
{
    public static WorldActorCatalogAuthoringDocument LoadActors()
    {
        return WorldActorJson.DeserializeActors(File.ReadAllText(ActorAuthoringPath));
    }

    public static WorldActorSpawnAuthoringDocument LoadSpawns(
        string worldId = "development-world-1")
    {
        return WorldActorJson.DeserializeSpawns(File.ReadAllText(SpawnAuthoringPath(worldId)));
    }

    public static WorldActorRuntimeDocument Compile(string worldId = "development-world-1")
    {
        return WorldActorCompiler.Compile(LoadActors(), LoadSpawns(worldId));
    }

    public static string ActorAuthoringJson => File.ReadAllText(ActorAuthoringPath);

    public static string SpawnAuthoringJson => File.ReadAllText(
        SpawnAuthoringPath("development-world-1"));

    public static string RuntimeJson => RuntimeJsonFor("development-world-1");

    public static string RuntimeJsonFor(string worldId)
    {
        return File.ReadAllText(RuntimePath(worldId));
    }

    private static string ActorAuthoringPath => Path.Combine(
        RepositoryPaths.SharedContent,
        "Authoring",
        "Actors",
        "core.world-actors.json");

    private static string SpawnAuthoringPath(string worldId) => Path.Combine(
        RepositoryPaths.Worlds,
        worldId,
        "Authoring",
        "actor-spawns.json");

    private static string RuntimePath(string worldId) => Path.Combine(
        RepositoryPaths.Worlds,
        worldId,
        "Runtime",
        "world-actors.json");
}
