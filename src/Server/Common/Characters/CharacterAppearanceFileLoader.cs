using System.Text.Json;
using System.Text.Json.Serialization;
using Mmorpg.WorldData.Characters;

namespace Mmorpg.Server.Common.Characters;

// Loads the character appearance content strictly: GameService checks every look it stores
// against it, and SimulationWorker dresses development bots from it.
public static class CharacterAppearanceFileLoader
{
    public const string DefaultRelativePath = "WorldData/Characters/appearance.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // A relative path is resolved next to the running assembly, where the build copies the content.
    public static string ResolvePath(string? configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? DefaultRelativePath : configuredPath;
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));
    }

    public static CharacterAppearanceCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The character appearance content does not exist.", path);
        }

        return Parse(File.ReadAllText(path));
    }

    public static CharacterAppearanceCatalog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The character appearance content is empty.");
        }

        CharacterAppearanceDocument document;
        try
        {
            using var parsed = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            RejectDuplicateProperties(parsed.RootElement, "$");
            document = JsonSerializer.Deserialize<CharacterAppearanceDocument>(json, SerializerOptions)
                ?? throw new InvalidDataException("The character appearance content has no root object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The character appearance content is malformed: {exception.Message}", exception);
        }

        try
        {
            return CharacterAppearanceCatalog.Create(document);
        }
        catch (CharacterAppearanceValidationException exception)
        {
            throw new InvalidDataException($"The character appearance content is invalid: {exception.Message}", exception);
        }
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
                        $"The character appearance content contains duplicate property '{property.Name}' at {path}.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index}]");
                index++;
            }
        }
    }
}
