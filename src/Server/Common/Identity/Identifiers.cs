using System.Diagnostics.CodeAnalysis;

namespace Mmorpg.Server.Common.Identity;

// The two identifier shapes that services exchange. Service identifiers name workers,
// runtimes, Shards, and similar infrastructure; content identifiers name catalog entries
// such as item definitions, Mob ids, and quest grants, and may contain dots.
public static class Identifiers
{
    public const int MaximumLength = 128;

    public static bool IsServiceIdentifier([NotNullWhen(true)] string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaximumLength
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    public static bool IsContentIdentifier([NotNullWhen(true)] string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaximumLength
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }
}
