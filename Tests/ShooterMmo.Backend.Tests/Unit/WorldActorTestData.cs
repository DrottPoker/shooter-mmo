using ShooterMmo.Tools.WorldActorCompiler;
using ShooterMmo.WorldData.Actors;

namespace ShooterMmo.Backend.Tests.Unit;

internal static class WorldActorTestData
{
    public static WorldActorCatalogAuthoringDocument LoadActors()
    {
        return WorldActorJson.DeserializeActors(File.ReadAllText(ActorAuthoringPath));
    }

    public static WorldActorSpawnAuthoringDocument LoadSpawns()
    {
        return WorldActorJson.DeserializeSpawns(File.ReadAllText(SpawnAuthoringPath));
    }

    public static WorldActorRuntimeDocument Compile()
    {
        return WorldActorCompiler.Compile(LoadActors(), LoadSpawns());
    }

    public static string ActorAuthoringJson => File.ReadAllText(ActorAuthoringPath);

    public static string SpawnAuthoringJson => File.ReadAllText(SpawnAuthoringPath);

    public static string RuntimeJson => File.ReadAllText(RuntimePath);

    private static string ActorAuthoringPath => Path.Combine(
        FindRepositoryRoot(),
        "WorldData",
        "Authoring",
        "Actors",
        "core.world-actors.json");

    private static string SpawnAuthoringPath => Path.Combine(
        FindRepositoryRoot(),
        "WorldData",
        "Authoring",
        "ActorSpawns",
        "local-world-1.actor-spawns.json");

    private static string RuntimePath => Path.Combine(
        FindRepositoryRoot(),
        "WorldData",
        "Runtime",
        "Actors",
        "local-world-1.world-actors.json");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ShooterMmo.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Shooter MMO repository root.");
    }
}
