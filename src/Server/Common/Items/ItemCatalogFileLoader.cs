using System.Text.Json;
using System.Text.Json.Serialization;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Server.Common.Items;

// Loads the compiled item catalog strictly and recompiles it from its own content, so a
// service trusts the revision and fingerprints instead of the file. GameService mirrors
// it into PostgreSQL, and SimulationWorker reads combat data and loot rules from it.
public static class ItemCatalogFileLoader
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

    public static ItemCatalogRuntimeDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The compiled item catalog does not exist.", path);
        }

        return Parse(File.ReadAllText(path));
    }

    public static async Task<ItemCatalogRuntimeDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The compiled item catalog does not exist.", path);
        }

        return Parse(await File.ReadAllTextAsync(path, cancellationToken));
    }

    public static ItemCatalogRuntimeDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The compiled item catalog is empty.");
        }

        ItemCatalogRuntimeDocument runtime;
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
            runtime = JsonSerializer.Deserialize<ItemCatalogRuntimeDocument>(json, SerializerOptions)
                ?? throw new InvalidDataException("The compiled item catalog has no root object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The compiled item catalog is malformed: {exception.Message}",
                exception);
        }

        return ItemCatalogRuntimeVerifier.VerifyAndCanonicalize(runtime);
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
                        $"The compiled item catalog contains duplicate property "
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
