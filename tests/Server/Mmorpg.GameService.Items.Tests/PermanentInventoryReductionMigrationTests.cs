using Dapper;
using Mmorpg.GameService.Items.Persistence;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// The permanent inventory shrinks from 24 back to 20 slots without losing an item. The
// fixture catalog keeps 20 slots, so the expansion migration first grows its characters to
// 24, as every character was before this migration.
public sealed class PermanentInventoryReductionMigrationTests
{
    private const string Ore = "fixture.ore";

    [PostgresIntegrationFact]
    public async Task MovesItemsOfTheRemovedSlotsToFreeSlotsAndThenToRecoveryStorage()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var crowded = (await world.RegisterAsync("reduction_crowded")).Character.Id;
        var roomy = (await world.RegisterAsync("reduction_roomy")).Character.Id;
        await world.ExecuteSqlAsync(PermanentInventoryExpansionMigration.Sql);

        // One slot that stays is free for the first of three items in removed slots.
        var crowdedState = await world.LoadStateAsync(crowded);
        var crowdedInventory = crowdedState.PermanentInventoryContainerId;
        for (var slotIndex = 0; slotIndex < 19; slotIndex++)
        {
            await world.GrantAsync(crowded, Ore, 1, crowdedInventory, slotIndex);
        }

        var first = await world.GrantAsync(crowded, Ore, 2, crowdedInventory, 20);
        var second = await world.GrantAsync(crowded, Ore, 3, crowdedInventory, 21);
        var third = await world.GrantAsync(crowded, Ore, 4, crowdedInventory, 23);

        var roomyInventory = (await world.LoadStateAsync(roomy)).PermanentInventoryContainerId;
        var kept = await world.GrantAsync(roomy, Ore, 5, roomyInventory, 0);
        var moved = await world.GrantAsync(roomy, Ore, 6, roomyInventory, 22);

        crowdedState = await world.LoadStateAsync(crowded);
        var roomyState = await world.LoadStateAsync(roomy);
        var crowdedRevision = await world.LoadContainerRevisionAsync(crowdedInventory);
        var recoveryRevision = await world.LoadContainerRevisionAsync(crowdedState.RecoveryStorageContainerId);
        var roomyRecoveryRevision = await world.LoadContainerRevisionAsync(roomyState.RecoveryStorageContainerId);

        await world.ExecuteSqlAsync(PermanentInventoryReductionMigration.Sql);

        Assert.Equal(20, await SettingsSlotCountAsync(context));
        foreach (var inventory in new[] { crowdedInventory, roomyInventory })
        {
            Assert.Equal(20, await CapacityAsync(context, inventory));
            Assert.Equal(Enumerable.Range(0, 20), await SlotIndexesAsync(context, inventory));
        }

        Assert.Equal(crowdedState.Revision + 1, (await world.LoadStateAsync(crowded)).Revision);
        Assert.Equal(crowdedRevision + 1, await world.LoadContainerRevisionAsync(crowdedInventory));
        Assert.Equal(recoveryRevision + 1, await world.LoadContainerRevisionAsync(crowdedState.RecoveryStorageContainerId));
        await AssertItemAsync(world, first, crowdedInventory, 19, 2);
        await AssertItemAsync(world, second, crowdedState.RecoveryStorageContainerId, 0, 3);
        await AssertItemAsync(world, third, crowdedState.RecoveryStorageContainerId, 1, 4);
        var delivery = Assert.Single(await DeliveriesAsync(context, crowded));
        Assert.Equal("permanent_inventory_reduction", delivery.SourceKind);
        Assert.Equal(PermanentInventoryReductionMigration.Id, delivery.SourceEventId);
        Assert.Equal(new[] { second, third }, await DeliveryItemsAsync(context, delivery.Id));

        // The lowest free slot takes the item, and nothing reaches Recovery Storage.
        Assert.Equal(roomyState.Revision + 1, (await world.LoadStateAsync(roomy)).Revision);
        await AssertItemAsync(world, kept, roomyInventory, 0, 5);
        await AssertItemAsync(world, moved, roomyInventory, 1, 6);
        Assert.Empty(await DeliveriesAsync(context, roomy));
        Assert.Equal(roomyRecoveryRevision, await world.LoadContainerRevisionAsync(roomyState.RecoveryStorageContainerId));

        // A second run finds no inventory of the old count.
        var migrated = await world.LoadStateAsync(crowded);
        await world.ExecuteSqlAsync(PermanentInventoryReductionMigration.Sql);

        Assert.Equal(migrated.Revision, (await world.LoadStateAsync(crowded)).Revision);
        Assert.Single(await DeliveriesAsync(context, crowded));
        Assert.Equal(20, await SettingsSlotCountAsync(context));
    }

    private static async Task AssertItemAsync(
        ItemScenarioWorld world,
        Guid itemInstanceId,
        Guid containerId,
        int slotIndex,
        int quantity)
    {
        var item = await world.LoadItemAsync(itemInstanceId);
        Assert.Equal(containerId, item.ContainerId);
        Assert.Equal(slotIndex, item.ContainerSlotIndex);
        Assert.Equal(quantity, item.Quantity);
    }

    private static async Task<int[]> SlotIndexesAsync(
        PostgresIntegrationTestContext context,
        Guid containerId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var slots = await connection.QueryAsync<int>(
            """
            select slot_index
            from item_container_slots
            where container_id = @ContainerId
            order by slot_index;
            """,
            new { ContainerId = containerId });
        return slots.ToArray();
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

    private static async Task<DeliveryRow[]> DeliveriesAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var deliveries = await connection.QueryAsync<DeliveryRow>(
            """
            select
                id as "Id",
                source_kind as "SourceKind",
                source_event_id as "SourceEventId"
            from recovery_deliveries
            where character_id = @CharacterId
            order by id;
            """,
            new { CharacterId = characterId });
        return deliveries.ToArray();
    }

    private static async Task<Guid[]> DeliveryItemsAsync(
        PostgresIntegrationTestContext context,
        Guid deliveryId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var items = await connection.QueryAsync<Guid>(
            """
            select item_instance_id
            from recovery_delivery_items
            where recovery_delivery_id = @DeliveryId
            order by item_order;
            """,
            new { DeliveryId = deliveryId });
        return items.ToArray();
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

    private sealed record DeliveryRow(Guid Id, string SourceKind, string SourceEventId);
}
