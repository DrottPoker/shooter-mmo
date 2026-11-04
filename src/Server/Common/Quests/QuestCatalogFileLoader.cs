using System.Text.Json;
using System.Text.Json.Serialization;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Server.Common.Quests;

// Loads the compiled quest catalog strictly and checks its revision and fingerprints
// against its own content, so a service trusts them instead of the file. GameService
// mirrors the quest identities into PostgreSQL and runs the quest commands with it.
public static class QuestCatalogFileLoader
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

    public static QuestCatalogRuntimeDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The compiled quest catalog does not exist.", path);
        }

        return Parse(File.ReadAllText(path));
    }

    public static QuestCatalogRuntimeDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The compiled quest catalog is empty.");
        }

        QuestCatalogRuntimeDocument runtime;
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            RejectDuplicateProperties(document.RootElement, "$");
            runtime = JsonSerializer.Deserialize<QuestCatalogRuntimeDocument>(json, SerializerOptions)
                ?? throw new InvalidDataException("The compiled quest catalog has no root object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The compiled quest catalog is malformed: {exception.Message}",
                exception);
        }

        try
        {
            QuestCatalogCompiler.ValidateRuntime(runtime);
        }
        catch (QuestValidationException exception)
        {
            throw new InvalidDataException(
                "The compiled quest catalog is invalid: " + string.Join(" ", exception.Errors),
                exception);
        }

        return runtime;
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"The compiled quest catalog contains duplicate property "
                        + $"'{property.Name}' at {path}.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
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
            RejectDuplicateProperties(item, $"{path}[{index}]");
            index++;
        }
    }
}
