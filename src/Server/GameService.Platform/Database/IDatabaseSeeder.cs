namespace Mmorpg.GameService.Platform.Database;

// Idempotent startup data that a module needs after every migration has run.
public interface IDatabaseSeeder
{
    Task SeedAsync(CancellationToken cancellationToken);
}
