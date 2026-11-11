using Mmorpg.WorldData.Text;

namespace Mmorpg.GameService.Content;

// The kinds of content file that GameService serves.
internal static class ContentFileKinds
{
    public const string ItemCatalog = "item-catalog";
    public const string QuestCatalog = "quest-catalog";
    public const string TextTable = "text-table";

    public static bool IsKnown(string? kind)
    {
        return kind is ItemCatalog or QuestCatalog or TextTable;
    }
}

// One served file: its exact bytes and the header fields the manifest lists.
internal sealed record ContentFile(
    string Kind,
    string Id,
    string? Locale,
    int FormatVersion,
    string Revision,
    byte[] Bytes)
{
    // The path below /api/content: "<kind>/<id>/<revision>", and for a text table
    // "text-table/<locale>/<id>/<revision>".
    public string RelativePath { get; } =
        ContentPaths.TryCreateRelativePath(Kind, Locale, Id, Revision)
        ?? throw new ArgumentException(
            $"Content file '{Kind}' '{Id}' has an id, locale, or revision that no route accepts.");
}

// The route rules, checked before any lookup and by the loader, so every listed URL resolves.
internal static class ContentPaths
{
    private const int MaximumIdLength = 64;
    private const int RevisionLength = 64;

    public static string? TryCreateRelativePath(
        string? kind,
        string? locale,
        string? id,
        string? revision)
    {
        if (!ContentFileKinds.IsKnown(kind) || !IsValidId(id) || !IsValidRevision(revision))
        {
            return null;
        }

        if (kind == ContentFileKinds.TextTable)
        {
            return locale is not null && TextTable.IsValidLocale(locale)
                ? $"{kind}/{locale}/{id}/{revision}"
                : null;
        }

        return locale is null ? $"{kind}/{id}/{revision}" : null;
    }

    // A lowercase id such as "core" or "ui": a letter, then letters, digits, '-', '_', or '.'.
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaximumIdLength || !char.IsAsciiLetterLower(id[0]))
        {
            return false;
        }

        return id.All(character => char.IsAsciiLetterLower(character)
            || char.IsAsciiDigit(character)
            || character is '-' or '_' or '.');
    }

    // A SHA-256 revision in lowercase hex.
    public static bool IsValidRevision(string? revision)
    {
        return revision is { Length: RevisionLength }
            && revision.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
    }
}
