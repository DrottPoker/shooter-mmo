using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mmorpg.GameService.Content;

// The served files and the manifest that lists them, built once. Only the current revision
// of each file exists.
internal sealed class ContentFileStore
{
    public const int ManifestFormatVersion = 1;
    public const string RoutePrefix = "/api/content";

    private static readonly JsonSerializerOptions ManifestSerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

    private readonly Dictionary<string, ContentFile> filesByPath;

    public ContentFileStore(IEnumerable<ContentFile> files, string? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(files);

        // Catalogs first, then the text tables by locale and id, so the manifest is stable.
        Files = files
            .OrderBy(file => KindOrder(file.Kind))
            .ThenBy(file => file.Locale ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(file => file.Id, StringComparer.Ordinal)
            .ToArray();
        filesByPath = Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);

        var urlPrefix = (baseUrl ?? RoutePrefix) + "/";
        var manifest = new ContentManifestResponse(
            ManifestFormatVersion,
            Files.Select(file => new ContentManifestFileResponse(
                    file.Kind,
                    file.Id,
                    file.Locale,
                    file.FormatVersion,
                    file.Revision,
                    file.Bytes.LongLength,
                    urlPrefix + file.RelativePath))
                .ToArray());
        ManifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestSerializerOptions);
        ManifestETag = $"\"{Convert.ToHexStringLower(SHA256.HashData(ManifestBytes))}\"";
    }

    public IReadOnlyList<ContentFile> Files { get; }

    public byte[] ManifestBytes { get; }

    // A strong ETag over the manifest bytes, so any change of a listed file changes it.
    public string ManifestETag { get; }

    public ContentFile? Find(string relativePath)
    {
        return filesByPath.GetValueOrDefault(relativePath);
    }

    public ContentFile? FindCatalog(string kind)
    {
        return Files.SingleOrDefault(file => string.Equals(file.Kind, kind, StringComparison.Ordinal));
    }

    private static int KindOrder(string kind)
    {
        return kind switch
        {
            ContentFileKinds.ItemCatalog => 0,
            ContentFileKinds.QuestCatalog => 1,
            _ => 2
        };
    }
}

internal sealed record ContentManifestResponse(
    int FormatVersion,
    IReadOnlyList<ContentManifestFileResponse> Files);

// Locale is set for text tables only and left out of the JSON otherwise.
internal sealed record ContentManifestFileResponse(
    string Kind,
    string Id,
    string? Locale,
    int FormatVersion,
    string Revision,
    long SizeBytes,
    string Url);
