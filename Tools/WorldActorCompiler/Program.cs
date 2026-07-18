using ShooterMmo.Tools.WorldActorCompiler;
using ShooterMmo.WorldData.Actors;

if (!TryResolveArguments(
        args,
        out var actorAuthoringPath,
        out var spawnAuthoringPath,
        out var runtimePath,
        out var verifyOnly,
        out var argumentError))
{
    Console.Error.WriteLine(argumentError);
    Console.Error.WriteLine(
        "Usage: WorldActorCompiler [<actors.json> <spawns.json> <runtime.json>] [--verify]");
    return 1;
}

try
{
    if (!File.Exists(actorAuthoringPath))
    {
        throw new FileNotFoundException(
            "World actor authoring file does not exist.",
            actorAuthoringPath);
    }

    if (!File.Exists(spawnAuthoringPath))
    {
        throw new FileNotFoundException(
            "World actor spawn authoring file does not exist.",
            spawnAuthoringPath);
    }

    var actorJson = await File.ReadAllTextAsync(actorAuthoringPath);
    var spawnJson = await File.ReadAllTextAsync(spawnAuthoringPath);
    var actors = WorldActorJson.DeserializeActors(actorJson);
    var spawns = WorldActorJson.DeserializeSpawns(spawnJson);
    var runtime = WorldActorCompiler.Compile(actors, spawns);
    var expectedRuntimeJson = WorldActorJson.SerializeRuntime(runtime);

    if (verifyOnly)
    {
        if (!File.Exists(runtimePath))
        {
            throw new InvalidDataException("Compiled world actor runtime file is stale.");
        }

        var actualRuntimeJson = await File.ReadAllTextAsync(runtimePath);
        _ = WorldActorJson.DeserializeRuntime(actualRuntimeJson);
        if (!string.Equals(
                NormalizeLineEndings(actualRuntimeJson),
                expectedRuntimeJson,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Compiled world actor runtime file is stale.");
        }

        Console.WriteLine(
            "World actors verified for world {0} at revision {1} with {2} definitions and {3} runtime instances.",
            runtime.WorldId,
            runtime.Revision,
            runtime.Actors.Length,
            runtime.SpawnInstances.Length);
        return 0;
    }

    var runtimeDirectory = Path.GetDirectoryName(runtimePath);
    if (!string.IsNullOrWhiteSpace(runtimeDirectory))
    {
        Directory.CreateDirectory(runtimeDirectory);
    }

    await File.WriteAllTextAsync(runtimePath, expectedRuntimeJson);
    Console.WriteLine(
        "World actors compiled for world {0} at revision {1} with {2} definitions and {3} runtime instances.",
        runtime.WorldId,
        runtime.Revision,
        runtime.Actors.Length,
        runtime.SpawnInstances.Length);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("World actor compilation failed: " + exception.Message);
    return 1;
}

static bool TryResolveArguments(
    string[] arguments,
    out string actorAuthoringPath,
    out string spawnAuthoringPath,
    out string runtimePath,
    out bool verifyOnly,
    out string error)
{
    verifyOnly = false;
    error = string.Empty;
    if (arguments.Length == 0
        || (arguments.Length == 1
            && string.Equals(arguments[0], "--verify", StringComparison.OrdinalIgnoreCase)))
    {
        verifyOnly = arguments.Length == 1;
        var root = FindRepositoryRoot(Directory.GetCurrentDirectory());
        actorAuthoringPath = Path.Combine(
            root,
            "WorldData",
            "Authoring",
            "Actors",
            "core.world-actors.json");
        spawnAuthoringPath = Path.Combine(
            root,
            "WorldData",
            "Authoring",
            "ActorSpawns",
            "local-world-1.actor-spawns.json");
        runtimePath = Path.Combine(
            root,
            "WorldData",
            "Runtime",
            "Actors",
            "local-world-1.world-actors.json");
        return true;
    }

    if (arguments.Length is 3 or 4)
    {
        verifyOnly = arguments.Length == 4
            && string.Equals(arguments[3], "--verify", StringComparison.OrdinalIgnoreCase);
        if (arguments.Length == 4 && !verifyOnly)
        {
            actorAuthoringPath = string.Empty;
            spawnAuthoringPath = string.Empty;
            runtimePath = string.Empty;
            error = "The only supported fourth argument is --verify.";
            return false;
        }

        actorAuthoringPath = Path.GetFullPath(arguments[0]);
        spawnAuthoringPath = Path.GetFullPath(arguments[1]);
        runtimePath = Path.GetFullPath(arguments[2]);
        return true;
    }

    actorAuthoringPath = string.Empty;
    spawnAuthoringPath = string.Empty;
    runtimePath = string.Empty;
    error = "WorldActorCompiler arguments are invalid.";
    return false;
}

static string FindRepositoryRoot(string startPath)
{
    var current = new DirectoryInfo(Path.GetFullPath(startPath));
    while (current != null)
    {
        if (File.Exists(Path.Combine(current.FullName, "ShooterMmo.slnx")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new DirectoryNotFoundException(
        "Could not find the shooter-mmo repository root from '" + startPath + "'.");
}

static string NormalizeLineEndings(string value)
{
    return value.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\r", "\n", StringComparison.Ordinal);
}
