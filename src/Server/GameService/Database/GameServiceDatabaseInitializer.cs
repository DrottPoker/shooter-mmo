using System.Globalization;
using System.Text;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Database;

// Applies the merged migration history, then every module's startup seed data in module
// order: everything that the migrate command and a start with
// Database:RunMigrationsOnStartup do.
//
// A database can be ahead of this build: it records migrations that the build does not contain,
// because a newer release migrated it. A start tolerates that, because the expand rule promises
// that the version before runs on the next schema, and reports the ids. The migrate command and
// its plan refuse it, because mirroring this build's catalogs over a newer release's would undo
// that release. A migrate-and-seed holds the runner's operation lock from its read of the
// recorded ids to its last seed, so a build that found the database not ahead cannot seed after
// a newer release migrated meanwhile: it waits for that release and then sees its ids.
public sealed class GameServiceDatabaseInitializer(
    GameServiceSchema schema,
    DatabaseMigrationRunner migrationRunner,
    IEnumerable<IDatabaseSeeder> seeders)
{
    // What a start with migrations runs. A database that is ahead of this build is migrated and
    // seeded all the same; the changes name its unknown ids.
    public Task<GameServiceDatabaseChanges> InitializeAsync(CancellationToken cancellationToken)
    {
        return InitializeAsync(refuseWhenAhead: false, cancellationToken);
    }

    // What the migrate command runs. A database that is ahead of this build is refused with a
    // DatabaseAheadOfBuildException before anything is applied or seeded.
    public Task<GameServiceDatabaseChanges> MigrateAsync(CancellationToken cancellationToken)
    {
        return InitializeAsync(refuseWhenAhead: true, cancellationToken);
    }

    // What InitializeAsync would change, without changing anything. The changes name the ids that
    // an ahead database records beyond this build.
    public Task<GameServiceDatabaseChanges> PlanAsync(CancellationToken cancellationToken)
    {
        return PlanAsync(refuseWhenAhead: false, cancellationToken);
    }

    // What the migrate plan reads. A database that is ahead of this build is refused with a
    // DatabaseAheadOfBuildException before a seeder reads anything.
    public Task<GameServiceDatabaseChanges> PlanUnlessAheadAsync(CancellationToken cancellationToken)
    {
        return PlanAsync(refuseWhenAhead: true, cancellationToken);
    }

    // A start without migrations refuses a database that the migrate command would still
    // change, and a database it cannot read. A database that is ahead of this build and has
    // nothing else pending passes: the ids that it records beyond this build are returned for the
    // caller to log. When such a database also has something pending, the refusal says that the
    // database is newer than this build and does not advise the migrate command, which refuses it.
    public async Task<IReadOnlyList<string>> EnsureCurrentAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> unknown = [];
        GameServiceDatabaseChanges plan;
        try
        {
            var migrationPlan = await migrationRunner.PlanAsync(schema.Migrations, cancellationToken);
            unknown = migrationPlan.Unknown;
            plan = await PlanSeedersAsync(migrationPlan, cancellationToken);
        }
        catch (NpgsqlException exception)
        {
            throw new InvalidOperationException(
                "GameService refuses to start: Database:RunMigrationsOnStartup is false, and it could not "
                + $"read the database to check that it is current: {exception.Message}"
                + (unknown.Count > 0 ? " " + DatabaseAheadOfBuildException.Describe(unknown) : string.Empty),
                exception);
        }

        if (plan.IsEmpty)
        {
            return plan.UnknownMigrations;
        }

        throw new InvalidOperationException(
            "GameService refuses to start: Database:RunMigrationsOnStartup is false, and the database "
            + "is not current." + Environment.NewLine
            + plan.Describe("Pending migrations", "Pending seed changes")
            + (plan.IsAhead
                ? DatabaseAheadOfBuildException.Describe(plan.UnknownMigrations)
                    + " This build cannot bring it up to date: start the newer release, or restore a "
                    + "backup from before that release migrated."
                : GameServiceDatabaseCommandLine.MigrateAdvice));
    }

    // What a start does with the database before it serves: with migrations, applies and seeds as
    // InitializeAsync does; without, refuses a database that is not current. A database that a
    // newer release migrated gets one warning that names its unknown ids, which are returned for
    // the caller to record.
    public async Task<IReadOnlyList<string>> PrepareForStartAsync(
        bool runMigrations,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);

        IReadOnlyList<string> unknown;
        if (runMigrations)
        {
            var changes = await InitializeAsync(cancellationToken);
            foreach (var change in changes.SeedChanges)
            {
                logger.LogInformation("Database seed change: {SeedChange}", change);
            }

            unknown = changes.UnknownMigrations;
        }
        else
        {
            unknown = await EnsureCurrentAsync(cancellationToken);
        }

        if (unknown.Count > 0)
        {
            logger.LogWarning(
                "The database is newer than this build: it records migrations that this build does not know, "
                + "{UnknownMigrations}, because a newer release migrated it. This build runs on it anyway, "
                + "because nothing else is pending; a pending migration or a seed difference would refuse a "
                + "start without migrations.",
                string.Join(", ", unknown));
        }

        return unknown;
    }

    private async Task<GameServiceDatabaseChanges> InitializeAsync(
        bool refuseWhenAhead,
        CancellationToken cancellationToken)
    {
        await using var operation = await migrationRunner.HoldOperationLockAsync(cancellationToken);
        var run = refuseWhenAhead
            ? await migrationRunner.ApplyUnlessAheadAsync(schema.Migrations, cancellationToken)
            : await migrationRunner.ApplyAsync(schema.Migrations, cancellationToken);
        var seedChanges = new List<string>();
        foreach (var seeder in seeders)
        {
            seedChanges.AddRange(await seeder.SeedAsync(cancellationToken));
        }

        return new GameServiceDatabaseChanges(run.Applied, seedChanges) { UnknownMigrations = run.Unknown };
    }

    private async Task<GameServiceDatabaseChanges> PlanAsync(bool refuseWhenAhead, CancellationToken cancellationToken)
    {
        var migrationPlan = await migrationRunner.PlanAsync(schema.Migrations, cancellationToken);
        if (refuseWhenAhead && migrationPlan.IsAhead)
        {
            throw new DatabaseAheadOfBuildException(migrationPlan.Unknown);
        }

        return await PlanSeedersAsync(migrationPlan, cancellationToken);
    }

    private async Task<GameServiceDatabaseChanges> PlanSeedersAsync(
        MigrationPlan migrationPlan,
        CancellationToken cancellationToken)
    {
        var seedChanges = new List<string>();
        foreach (var seeder in seeders)
        {
            seedChanges.AddRange(await seeder.PlanAsync(cancellationToken));
        }

        return new GameServiceDatabaseChanges(migrationPlan.Pending, seedChanges)
        {
            UnknownMigrations = migrationPlan.Unknown
        };
    }
}

// The migrations and seed changes that a run applied or a plan found, one line each. The unknown
// migrations are the ids that the database records beyond this build, which a newer release
// migrated; they are not changes, so they never make the changes non-empty.
public sealed record GameServiceDatabaseChanges(
    IReadOnlyList<string> Migrations,
    IReadOnlyList<string> SeedChanges)
{
    public IReadOnlyList<string> UnknownMigrations { get; init; } = [];

    public bool IsAhead => UnknownMigrations.Count > 0;

    public bool IsEmpty => Migrations.Count == 0 && SeedChanges.Count == 0;

    public string Describe(string migrationsTitle, string seedChangesTitle)
    {
        var builder = new StringBuilder();
        Append(builder, migrationsTitle, Migrations);
        Append(builder, seedChangesTitle, SeedChanges);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string title, IReadOnlyList<string> lines)
    {
        builder.Append(title)
            .Append(": ")
            .AppendLine(lines.Count == 0 ? "none" : lines.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var line in lines)
        {
            builder.Append("  ").AppendLine(line);
        }
    }
}
