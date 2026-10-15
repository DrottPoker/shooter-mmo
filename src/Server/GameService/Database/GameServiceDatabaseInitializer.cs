using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Database;

// Applies the merged migration history, then every module's startup seed data in
// module order.
public sealed class GameServiceDatabaseInitializer(
    GameServiceSchema schema,
    DatabaseMigrationRunner migrationRunner,
    IEnumerable<IDatabaseSeeder> seeders)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await migrationRunner.ApplyAsync(schema.Migrations, cancellationToken);
        foreach (var seeder in seeders)
        {
            await seeder.SeedAsync(cancellationToken);
        }
    }
}
