using System.Text.Json;
using System.Text.Json.Serialization;
using Mmorpg.WorldData.Progression;

namespace Mmorpg.Server.Common.Progression;

// Loads the progression rules strictly: GameService computes every level and experience
// result from them.
public static class ProgressionRulesFileLoader
{
    public const string DefaultRelativePath = "WorldData/Progression/progression-rules.json";

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

    public static ProgressionRules Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The progression rules do not exist.", path);
        }

        return Parse(File.ReadAllText(path));
    }

    public static ProgressionRules Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The progression rules are empty.");
        }

        ProgressionRulesDocument document;
        try
        {
            using var parsed = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            RejectDuplicateProperties(parsed.RootElement, "$");
            document = JsonSerializer.Deserialize<ProgressionRulesDocument>(json, SerializerOptions)
                ?? throw new InvalidDataException("The progression rules have no root object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The progression rules are malformed: {exception.Message}", exception);
        }

        try
        {
            return ProgressionRules.FromDocument(document);
        }
        catch (ProgressionRulesValidationException exception)
        {
            throw new InvalidDataException($"The progression rules are invalid: {exception.Message}", exception);
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
                        $"The progression rules contain duplicate property '{property.Name}' at {path}.");
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
