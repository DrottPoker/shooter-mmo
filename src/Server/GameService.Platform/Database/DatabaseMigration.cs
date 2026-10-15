namespace Mmorpg.GameService.Platform.Database;

// One schema migration. The id is recorded in schema_migrations, so an applied
// migration's id and SQL never change.
public sealed record DatabaseMigration(string Id, string Sql);
