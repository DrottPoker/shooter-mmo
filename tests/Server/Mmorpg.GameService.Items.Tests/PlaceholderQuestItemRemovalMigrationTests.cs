using Dapper;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Items.Persistence;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// The quest that every quest NPC offered before quest content existed is gone, so the letters
// it granted leave the inventory, the Bank, and Recovery Storage with a destruction record,
// while the items of real quests stay.
public sealed class PlaceholderQuestItemRemovalMigrationTests
{
    private const string Letter = "fixture.letter";
    private const string PlaceholderQuest = "quest.local.sealed_letter";

    // The placeholder granted at most one letter per character, which its owner may have moved to
    // the Bank or a migration to Recovery Storage.
    [PostgresIntegrationFact]
    public async Task RemovesThePlaceholderLettersAndKeepsTheItemsOfRealQuests()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var carrier = (await world.RegisterAsync("placeholder_carrier")).Character.Id;
        var banker = (await world.RegisterAsync("placeholder_banker")).Character.Id;
        var recoverer = (await world.RegisterAsync("placeholder_recoverer")).Character.Id;
        var carrierState = await world.LoadStateAsync(carrier);
        var carried = await GrantPlaceholderAsync(world, carrier, carrierState.PermanentInventoryContainerId);
        var kept = await world.GrantAsync(
            carrier,
            Letter,
            1,
            carrierState.PermanentInventoryContainerId,
            1,
            ItemPolicySourceKinds.QuestGrant,
            "quest.fixture.errand");
        var bankerState = await world.LoadStateAsync(banker);
        var banked = await GrantPlaceholderAsync(world, banker, bankerState.BankContainerId);
        var recovererState = await world.LoadStateAsync(recoverer);
        var recovered = await GrantPlaceholderAsync(world, recoverer, recovererState.PermanentInventoryContainerId);
        await world.AddRecoveryDeliveryAsync(recoverer, recovered);
        var before = new Dictionary<Guid, long>();
        foreach (var characterId in new[] { carrier, banker, recoverer })
        {
            before[characterId] = (await world.LoadStateAsync(characterId)).Revision;
        }

        var bankRevision = await world.LoadContainerRevisionAsync(bankerState.BankContainerId);
        var inventoryRevision = await world.LoadContainerRevisionAsync(carrierState.PermanentInventoryContainerId);

        await world.ExecuteSqlAsync(PlaceholderQuestItemRemovalMigration.Sql);

        foreach (var removed in new[] { carried, banked, recovered })
        {
            Assert.False(await ExistsAsync(context, removed));
            Assert.Equal("quest_retired", await DestructionReasonAsync(context, removed));
        }

        Assert.True(await ExistsAsync(context, kept));
        foreach (var (characterId, revision) in before)
        {
            Assert.Equal(revision + 1, (await world.LoadStateAsync(characterId)).Revision);
        }

        Assert.Equal(bankRevision + 1, await world.LoadContainerRevisionAsync(bankerState.BankContainerId));
        Assert.Equal(inventoryRevision + 1, await world.LoadContainerRevisionAsync(carrierState.PermanentInventoryContainerId));
        Assert.Equal(0, await RecoveryRowsAsync(context, recovererState.RecoveryStorageContainerId));

        // A second run finds nothing to remove.
        await world.ExecuteSqlAsync(PlaceholderQuestItemRemovalMigration.Sql);

        foreach (var (characterId, revision) in before)
        {
            Assert.Equal(revision + 1, (await world.LoadStateAsync(characterId)).Revision);
        }

        Assert.True(await ExistsAsync(context, kept));
    }

    private static Task<Guid> GrantPlaceholderAsync(ItemScenarioWorld world, Guid characterId, Guid containerId)
    {
        return world.GrantAsync(characterId, Letter, 1, containerId, 0, ItemPolicySourceKinds.QuestGrant, PlaceholderQuest);
    }

    private static async Task<bool> ExistsAsync(PostgresIntegrationTestContext context, Guid itemInstanceId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<bool>(
            "select exists (select 1 from item_instances where id = @ItemInstanceId);",
            new { ItemInstanceId = itemInstanceId });
    }

    private static async Task<string?> DestructionReasonAsync(PostgresIntegrationTestContext context, Guid itemInstanceId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "select reason from item_destructions where item_instance_id = @ItemInstanceId;",
            new { ItemInstanceId = itemInstanceId });
    }

    // The Recovery Storage slots and unclaimed deliveries that are left.
    private static async Task<int> RecoveryRowsAsync(PostgresIntegrationTestContext context, Guid recoveryContainerId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            """
            select
                (select count(*) from item_container_slots where container_id = @ContainerId)
                + (select count(*)
                   from recovery_deliveries
                   where recovery_storage_container_id = @ContainerId
                     and claimed_at is null);
            """,
            new { ContainerId = recoveryContainerId });
    }
}
