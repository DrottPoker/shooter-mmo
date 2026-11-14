namespace Mmorpg.GameService.Platform.Database;

// Startup data that a module needs after every migration has run: content that it mirrors,
// or a check or recompute of stored data that runs only when that content changed.
public interface IDatabaseSeeder
{
    // What SeedAsync would change, one line each, without changing anything; empty when the
    // database already holds what this process brings. It may run before the migrations, so
    // it treats a missing table as holding nothing.
    Task<IReadOnlyList<string>> PlanAsync(CancellationToken cancellationToken);

    // Seeds idempotently and returns what it changed, one line each.
    Task<IReadOnlyList<string>> SeedAsync(CancellationToken cancellationToken);
}
