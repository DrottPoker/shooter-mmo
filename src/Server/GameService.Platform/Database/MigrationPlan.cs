namespace Mmorpg.GameService.Platform.Database;

// What a database lacks and what it has beyond a build's migrations. Pending are the ids that the
// build contains and the database has not recorded, in order. Unknown are the ids that the database
// recorded and the build does not contain: a newer release migrated it.
public sealed record MigrationPlan(IReadOnlyList<string> Pending, IReadOnlyList<string> Unknown)
{
    public bool IsAhead => Unknown.Count > 0;
}

// The ids that a run applied, and the recorded ids that its migrations do not contain.
public sealed record MigrationRun(IReadOnlyList<string> Applied, IReadOnlyList<string> Unknown);
