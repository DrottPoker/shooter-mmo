namespace Mmorpg.GameService.Platform.Database;

// The migrations that the database records beyond this build, which a newer release migrated, as
// the start found them. The start records them once, before the hosted services run. What judges
// data with this build's own rules, such as the economy audit, reads them and leaves such a
// database alone, because the expand rule lets this build run on a newer schema but does not make
// its judgment of what the newer release wrote right.
public sealed class DatabaseAheadState
{
    private volatile IReadOnlyList<string> unknownMigrations = [];

    public IReadOnlyList<string> UnknownMigrations => unknownMigrations;

    public bool IsAhead => unknownMigrations.Count > 0;

    public void Record(IReadOnlyList<string> unknown)
    {
        ArgumentNullException.ThrowIfNull(unknown);
        unknownMigrations = unknown;
    }
}
