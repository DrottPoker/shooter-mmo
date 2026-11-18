using System.Globalization;

namespace Mmorpg.GameService.Platform.Database;

// A database that records migrations which this build does not contain, because a newer release
// migrated it. This build cannot know what that release changed, so it neither migrates nor
// seeds over it.
public sealed class DatabaseAheadOfBuildException(IReadOnlyList<string> unknownMigrations)
    : InvalidOperationException(
        Describe(unknownMigrations)
        + " This build applies no migration and seeds nothing over it: start the newer release, "
        + "or restore a backup from before that release migrated.")
{
    private const int NamedIds = 10;

    public IReadOnlyList<string> UnknownMigrations { get; } = unknownMigrations;

    // One sentence that names the recorded ids that this build does not know.
    public static string Describe(IReadOnlyList<string> unknownMigrations)
    {
        ArgumentNullException.ThrowIfNull(unknownMigrations);
        var named = string.Join(", ", unknownMigrations.Take(NamedIds));
        var more = unknownMigrations.Count > NamedIds
            ? string.Create(CultureInfo.InvariantCulture, $", and {unknownMigrations.Count - NamedIds} more")
            : string.Empty;
        var count = unknownMigrations.Count;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"The database is newer than this build: it records {count} migration{(count == 1 ? string.Empty : "s")} that this build does not know ({named}{more}), because a newer release migrated it.");
    }
}
