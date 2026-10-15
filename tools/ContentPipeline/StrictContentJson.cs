using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mmorpg.Tools.ContentPipeline;

// Strict JSON for authored content: unknown members, duplicate properties, comments, and
// trailing commas all fail, so a typo can never be dropped silently.
public static class StrictContentJson
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

    public static T Deserialize<T>(string json, string documentLabel)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException($"{documentLabel} JSON is empty.");
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
            RejectDuplicateProperties(document.RootElement, "$", documentLabel);
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw new InvalidDataException($"{documentLabel} JSON has no root object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{documentLabel} JSON is malformed: {exception.Message}",
                exception);
        }
    }

    public static T DeserializeFile<T>(string path, string documentLabel)
        where T : class
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{documentLabel} file does not exist.", path);
        }

        try
        {
            return Deserialize<T>(File.ReadAllText(path), documentLabel);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{path}: {exception.Message}", exception);
        }
    }

    public static string Serialize<T>(T document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return NormalizeLineEndings(JsonSerializer.Serialize(document, SerializerOptions)) + "\n";
    }

    public static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }

    private static void RejectDuplicateProperties(
        JsonElement element,
        string path,
        string documentLabel)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"{documentLabel} JSON contains duplicate property "
                        + $"'{property.Name}' at {path}.");
                }

                RejectDuplicateProperties(
                    property.Value,
                    path + "." + property.Name,
                    documentLabel);
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
            RejectDuplicateProperties(item, path + "[" + index + "]", documentLabel);
            index++;
        }
    }
}
