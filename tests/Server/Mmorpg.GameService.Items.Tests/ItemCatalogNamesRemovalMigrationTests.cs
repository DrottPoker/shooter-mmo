using Dapper;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// GameService keeps the rules of the item catalog and none of its text, so a renamed item
// changes nothing in the database.
public sealed class ItemCatalogNamesRemovalMigrationTests
{
    [PostgresIntegrationFact]
    public async Task NoItemCatalogTableKeepsAName()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await using var connection = await context.DataSource.OpenConnectionAsync();

        var columns = (await connection.QueryAsync<string>(
            """
            select table_name || '.' || column_name
            from information_schema.columns
            where table_schema = current_schema()
              and table_name in (
                  'item_categories',
                  'item_tags',
                  'item_tool_kinds',
                  'equipment_slots',
                  'item_definitions',
                  'vendor_profiles',
                  'item_system_settings')
              and column_name like '%display_name%'
            order by 1;
            """)).ToArray();
        var checks = (await connection.QueryAsync<string>(
            """
            select conname
            from pg_constraint
            where connamespace = current_schema()::regnamespace
              and conname like 'ck\_%display\_name' escape '\'
              and conrelid::regclass::text in (
                  'item_categories',
                  'item_tags',
                  'item_tool_kinds',
                  'equipment_slots',
                  'item_definitions',
                  'vendor_profiles',
                  'item_system_settings')
            order by 1;
            """)).ToArray();

        Assert.Empty(columns);
        Assert.Empty(checks);

        // The seeded catalog is whole without the names: a character still gets its items.
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("nameless");
        Assert.NotEqual(Guid.Empty, (await world.LoadStateAsync(player.Character.Id)).PermanentInventoryContainerId);
    }
}
