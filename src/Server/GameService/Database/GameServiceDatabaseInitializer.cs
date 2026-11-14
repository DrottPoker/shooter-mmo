using System.Globalization;
using System.Text;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Database;

// Applies the merged migration history, then every module's startup seed data in module
// order: everything that the migrate command and a start with
// Database:RunMigrationsOnStartup do.
public sealed class GameServiceDatabaseInitializer(
    GameServiceSchema schema,
    DatabaseMigrationRunner migrationRunner,
    IEnumerable<IDatabaseSeeder> seeders)
{
    public async Task<GameServiceDatabaseChanges> InitializeAsync(CancellationToken cancellationToken)
    {
        var migrations = await migrationRunner.ApplyAsync(schema.Migrations, cancellationToken);
        var seedChanges = new List<string>();
        foreach (var seeder in seeders)
        {
            seedChanges.AddRange(await seeder.SeedAsync(cancellationToken));
        }

        return new GameServiceDatabaseChanges(migrations, seedChanges);
    }

    // What InitializeAsync would change, without changing anything.
    public async Task<GameServiceDatabaseChanges> PlanAsync(CancellationToken cancellationToken)
    {
        var migrations = await migrationRunner.PendingAsync(schema.Migrations, cancellationToken);
        var seedChanges = new List<string>();
        foreach (var seeder in seeders)
        {
            seedChanges.AddRange(await seeder.PlanAsync(cancellationToken));
        }

        return new GameServiceDatabaseChanges(migrations, seedChanges);
    }

    // A start without migrations refuses a database that the migrate command would still
    // change, and a database it cannot read.
    public async Task EnsureCurrentAsync(CancellationToken cancellationToken)
    {
        GameServiceDatabaseChanges plan;
        try
        {
            plan = await PlanAsync(cancellationToken);
        }
        catch (NpgsqlException exception)
        {
            throw new InvalidOperationException(
                "GameService refuses to start: Database:RunMigrationsOnStartup is false, and it could not "
                + $"read the database to check that it is current: {exception.Message}",
                exception);
        }

        if (!plan.IsEmpty)
        {
            throw new InvalidOperationException(
                "GameService refuses to start: Database:RunMigrationsOnStartup is false, and the database "
                + "is not current." + Environment.NewLine
                + plan.Describe("Pending migrations", "Pending seed changes")
                + GameServiceDatabaseCommandLine.MigrateAdvice);
        }
    }
}

// The migrations and seed changes that a run applied or a plan found, one line each.
public sealed record GameServiceDatabaseChanges(
    IReadOnlyList<string> Migrations,
    IReadOnlyList<string> SeedChanges)
{
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
