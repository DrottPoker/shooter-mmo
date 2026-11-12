using Dapper;
using Mmorpg.GameService.Items.Persistence;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// The permanent inventory grows from 20 to 24 slots without moving an item. The fixture
// catalog keeps 20 slots, so its characters start as every character did before the
// migration, which the test then runs again on them.
public sealed class PermanentInventoryExpansionMigrationTests
{
    private const string Ore = "fixture.ore";

    [PostgresIntegrationFact]
    public async Task ExpandsEveryTwentySlotInventoryAndKeepsItsItems()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("inventory_expansion");
        var characterId = player.Character.Id;
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        var ore = await world.GrantAsync(characterId, Ore, 3, inventoryId, 19);
        var before = await world.LoadStateAsync(characterId);
        var containerRevision = await world.LoadContainerRevisionAsync(inventoryId);
        Assert.Equal(Enumerable.Range(0, 20), await SlotIndexesAsync(context, inventoryId));

        await world.ExecuteSqlAsync(PermanentInventoryExpansionMigration.Sql);

        var after = await world.LoadStateAsync(characterId);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(containerRevision + 1, await world.LoadContainerRevisionAsync(inventoryId));
        Assert.Equal(24, await CapacityAsync(context, inventoryId));
        Assert.Equal(Enumerable.Range(0, 24), await SlotIndexesAsync(context, inventoryId));
        Assert.Equal(24, await SettingsSlotCountAsync(context));
        var item = await world.LoadItemAsync(ore);
        Assert.Equal(inventoryId, item.ContainerId);
        Assert.Equal(19, item.ContainerSlotIndex);
        Assert.Equal(3, item.Quantity);

        // A new slot takes items like any other.
        await world.GrantAsync(characterId, Ore, 1, inventoryId, 23);
        var granted = await world.LoadStateAsync(characterId);

        await world.ExecuteSqlAsync(PermanentInventoryExpansionMigration.Sql);

        Assert.Equal(granted.Revision, (await world.LoadStateAsync(characterId)).Revision);
        Assert.Equal(Enumerable.Range(0, 24), await SlotIndexesAsync(context, inventoryId));
        Assert.Equal(24, await SettingsSlotCountAsync(context));
    }

    private static async Task<int[]> SlotIndexesAsync(
        PostgresIntegrationTestContext context,
        Guid containerId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var slots = await connection.QueryAsync<(int SlotIndex, string SlotKind)>(
            """
            select slot_index, slot_kind
            from item_container_slots
            where container_id = @ContainerId
            order by slot_index;
            """,
            new { ContainerId = containerId });
        Assert.All(slots, slot => Assert.Equal("general", slot.SlotKind));
        return slots.Select(slot => slot.SlotIndex).ToArray();
    }

    private static async Task<int> CapacityAsync(
        PostgresIntegrationTestContext context,
        Guid containerId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            "select slot_capacity from item_containers where id = @ContainerId;",
            new { ContainerId = containerId });
    }

    private static async Task<int> SettingsSlotCountAsync(PostgresIntegrationTestContext context)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            """
            select permanent_inventory_slot_count
            from item_system_settings
            where id = 'character_default';
            """);
    }
}
