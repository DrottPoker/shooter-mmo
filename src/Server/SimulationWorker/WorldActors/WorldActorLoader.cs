using System.Text.Json;
using System.Text.Json.Serialization;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.WorldActors;

// Loads the shared actor catalog and the configured World's spawns, and checks that the spawns
// fit the catalog: the compiled files are separate, so a stale spawn file must fail here.
public static class WorldActorLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static WorldActorRuntime Load(SimulationWorkerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Load(config.ActorCatalogPath, config.ActorSpawnsPath, config.WorldId);
    }

    internal static WorldActorRuntime Load(
        string catalogPath,
        string spawnsPath,
        string expectedWorldId)
    {
        var catalog = Read<WorldActorCatalogRuntimeDocument>(
            catalogPath,
            "World actor catalog");
        var spawns = Read<WorldActorSpawnRuntimeDocument>(
            spawnsPath,
            "World actor spawn data");
        var runtime = new WorldActorRuntime(catalog, spawns);
        Validate(runtime, expectedWorldId);
        return runtime;
    }

    internal static void Validate(WorldActorRuntime runtime, string expectedWorldId)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        try
        {
            WorldActorCompiler.ValidateRuntime(runtime);
        }
        catch (WorldActorValidationException exception)
        {
            throw new InvalidDataException(
                "World actor runtime data failed structural validation: "
                    + exception.Message,
                exception);
        }

        if (!string.Equals(runtime.Spawns.WorldId, expectedWorldId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "World actor runtime data is incompatible with the configured world.");
        }
    }

    private static T Read<T>(string configuredPath, string label)
        where T : class
    {
        var path = Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{label} does not exist at '{path}'.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), SerializerOptions)
                ?? throw new InvalidDataException($"{label} has no root object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{label} is malformed: {exception.Message}",
                exception);
        }
    }
}
