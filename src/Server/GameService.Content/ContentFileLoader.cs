using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmorpg.Server.Common.Items;
using Mmorpg.Server.Common.Quests;
using Mmorpg.WorldData.Text;

namespace Mmorpg.GameService.Content;

// Reads every served file once at startup. The catalogs are checked with the same loaders
// the Items and Quests modules use, and every text table with TextTable.Validate. Any
// problem stops the start, so GameService never lists a file it cannot serve.
internal static class ContentFileLoader
{
    private static readonly JsonSerializerOptions TextTableSerializerOptions = new()
    {
        AllowTrailingCommas = false,
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ContentFileStore Load(ContentModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        var files = new List<ContentFile>();
        AddIfLoaded(files, errors, options.ItemCatalogPath, LoadItemCatalog);
        AddIfLoaded(files, errors, options.QuestCatalogPath, LoadQuestCatalog);
        files.AddRange(LoadTextTables(options.TextTablesPath, errors));
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "GameService cannot serve its content files:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new ContentFileStore(files, options.BaseUrl);
    }

    private static void AddIfLoaded(
        List<ContentFile> files,
        List<string> errors,
        string path,
        Func<byte[], ContentFile> load)
    {
        if (!File.Exists(path))
        {
            errors.Add($"The content file '{path}' does not exist.");
            return;
        }

        try
        {
            files.Add(load(File.ReadAllBytes(path)));
        }
        catch (Exception exception) when (exception is InvalidDataException
                                              or JsonException
                                              or ArgumentException
                                              or TextTableValidationException)
        {
            errors.Add($"The content file '{path}' is invalid: {exception.Message}");
        }
    }

    private static ContentFile LoadItemCatalog(byte[] bytes)
    {
        var header = ReadHeader(bytes, "catalogId");
        var catalog = ItemCatalogFileLoader.Parse(Encoding.UTF8.GetString(bytes));
        RequireHeaderMatches(header, catalog.CatalogId, catalog.FormatVersion, catalog.Revision);
        return new ContentFile(
            ContentFileKinds.ItemCatalog,
            catalog.CatalogId,
            null,
            catalog.FormatVersion,
            catalog.Revision,
            bytes);
    }

    private static ContentFile LoadQuestCatalog(byte[] bytes)
    {
        var header = ReadHeader(bytes, "catalogId");
        var catalog = QuestCatalogFileLoader.Parse(Encoding.UTF8.GetString(bytes));
        RequireHeaderMatches(header, catalog.CatalogId, catalog.FormatVersion, catalog.Revision);
        return new ContentFile(
            ContentFileKinds.QuestCatalog,
            catalog.CatalogId,
            null,
            catalog.FormatVersion,
            catalog.Revision,
            bytes);
    }

    private static IEnumerable<ContentFile> LoadTextTables(string directory, List<string> errors)
    {
        if (!Directory.Exists(directory))
        {
            errors.Add($"The text table folder '{directory}' does not exist.");
            return [];
        }

        var paths = Directory
            .EnumerateFiles(directory, "*" + TextTableFormat.FileSuffix, SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
        {
            errors.Add($"The text table folder '{directory}' holds no '*{TextTableFormat.FileSuffix}' file.");
            return [];
        }

        var tables = new List<ContentFile>();
        foreach (var path in paths)
        {
            AddIfLoaded(tables, errors, path, LoadTextTable);
        }

        foreach (var duplicate in tables
                     .GroupBy(table => (table.Locale, table.Id))
                     .Where(group => group.Count() > 1))
        {
            errors.Add(
                $"The text table '{duplicate.Key.Id}' for locale '{duplicate.Key.Locale}' "
                + $"is listed {duplicate.Count()} times in '{directory}'.");
        }

        return tables;
    }

    private static ContentFile LoadTextTable(byte[] bytes)
    {
        var table = JsonSerializer.Deserialize<TextTableDocument>(bytes, TextTableSerializerOptions)
            ?? throw new InvalidDataException("The text table has no root object.");
        TextTable.Validate(table);
        return new ContentFile(
            ContentFileKinds.TextTable,
            table.TableId,
            table.Locale,
            table.FormatVersion,
            table.Revision,
            bytes);
    }

    private static FileHeader ReadHeader(byte[] bytes, string idProperty)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The file has no root object.");
        }

        return new FileHeader(
            ReadString(root, idProperty),
            root.TryGetProperty("formatVersion", out var formatVersion)
                && formatVersion.ValueKind == JsonValueKind.Number
                && formatVersion.TryGetInt32(out var version)
                    ? version
                    : null,
            ReadString(root, "revision"));
    }

    private static string? ReadString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    // The served header must state what the module loader computed, or a client would
    // cache the file under a revision that does not describe it.
    private static void RequireHeaderMatches(
        FileHeader header,
        string id,
        int formatVersion,
        string revision)
    {
        if (!string.Equals(header.Id, id, StringComparison.Ordinal)
            || header.FormatVersion != formatVersion
            || !string.Equals(header.Revision, revision, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The file header states catalog '{header.Id}' format {header.FormatVersion} "
                + $"revision '{header.Revision}', but its content is catalog '{id}' format "
                + $"{formatVersion} revision '{revision}'.");
        }
    }

    private sealed record FileHeader(string? Id, int? FormatVersion, string? Revision);
}
