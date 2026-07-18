using System.Text.Json;
using System.Text.Json.Serialization;
using ShooterMmo.WorldData.Actors;

namespace ShooterMmo.Tools.WorldActorCompiler;

public static class WorldActorJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static WorldActorCatalogAuthoringDocument DeserializeActors(string json)
    {
        return Deserialize<WorldActorCatalogAuthoringDocument>(json, "actor authoring");
    }

    public static WorldActorSpawnAuthoringDocument DeserializeSpawns(string json)
    {
        return Deserialize<WorldActorSpawnAuthoringDocument>(json, "spawn authoring");
    }

    public static WorldActorRuntimeDocument DeserializeRuntime(string json)
    {
        return Deserialize<WorldActorRuntimeDocument>(json, "runtime");
    }

    public static string SerializeActors(WorldActorCatalogAuthoringDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Serialize(document);
    }

    public static string SerializeSpawns(WorldActorSpawnAuthoringDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Serialize(document);
    }

    public static string SerializeRuntime(WorldActorRuntimeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Serialize(document);
    }

    private static string Serialize<T>(T document)
    {
        return NormalizeLineEndings(JsonSerializer.Serialize(document, SerializerOptions)) + "\n";
    }

    private static T Deserialize<T>(string json, string documentKind)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException($"World actor {documentKind} JSON is empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            RejectDuplicateProperties(document.RootElement, "$", documentKind);
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw new InvalidDataException(
                    $"World actor {documentKind} JSON has no root object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"World actor {documentKind} JSON is malformed: {exception.Message}",
                exception);
        }
    }

    private static void RejectDuplicateProperties(
        JsonElement element,
        string path,
        string documentKind)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"World actor {documentKind} JSON contains duplicate property "
                        + $"'{property.Name}' at {path}.");
                }

                RejectDuplicateProperties(
                    property.Value,
                    path + "." + property.Name,
                    documentKind);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            RejectDuplicateProperties(item, path + "[" + index + "]", documentKind);
            index++;
        }
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }
}
