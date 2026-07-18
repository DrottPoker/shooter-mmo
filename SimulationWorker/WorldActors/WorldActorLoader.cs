using System.Text.Json;
using System.Text.Json.Serialization;
using ShooterMmo.WorldData.Actors;
using SimulationWorker.Config;

namespace SimulationWorker.WorldActors;

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

    public static WorldActorRuntimeDocument Load(SimulationWorkerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var path = Path.GetFullPath(
            config.ActorDataPath,
            AppContext.BaseDirectory);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"World actor runtime data does not exist at '{path}'.");
        }

        WorldActorRuntimeDocument document;
        try
        {
            document = JsonSerializer.Deserialize<WorldActorRuntimeDocument>(
                File.ReadAllText(path),
                SerializerOptions)
                ?? throw new InvalidDataException("World actor runtime data has no root object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "World actor runtime data is malformed: " + exception.Message,
                exception);
        }

        Validate(document, config.WorldId);
        return document;
    }

    internal static void Validate(WorldActorRuntimeDocument document, string expectedWorldId)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            WorldActorCompiler.ValidateRuntime(document);
        }
        catch (WorldActorValidationException exception)
        {
            throw new InvalidDataException(
                "World actor runtime data failed structural validation: "
                    + exception.Message,
                exception);
        }

        if (!string.Equals(document.WorldId, expectedWorldId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "World actor runtime data is incompatible with the configured world.");
        }
    }
}
