using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items.StackStress;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// The loot hotspot of the stack stress workloads goes through the same durable Mob corpse
// validation as a Mob's death, so the fixture cannot drift from that validation unnoticed.
public sealed class StackStressFixtureIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task TheLootHotspotFixtureCreatesADurableMobCorpseWithItsLoot()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        // A join registers the worker runtime whose lease the corpse command checks.
        await world.JoinAsync(await world.RegisterAsync("stress_hotspot"));
        var service = new StackStressFixtureService(
            context.ItemQueryService,
            context.ItemTransactionService,
            context.Services.GetRequiredService<SimulationWorkerLeasePolicy>(),
            new StackStressFixtureOptions(true, "stack-stress-test-fixture-secret-0000")
            {
                InventoryDefinitionId = "fixture.potion",
                LootDefinitionId = "fixture.ore"
            });

        var result = await service.CreateLootHotspotAsync(
            new StackStressLootHotspotRequest(
                "hotspot1",
                ItemScenarioWorld.WorkerId,
                ItemScenarioWorld.WorkerRuntimeId,
                ItemScenarioWorld.ShardId,
                0d,
                0d,
                0d,
                120d),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var hotspot = result.Value!;
        Assert.Equal("fixture.ore", hotspot.LootDefinitionId);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var presentationKey = await connection.QuerySingleAsync<string>(
            "select presentation_key from corpses where id = @CorpseId;",
            new { hotspot.CorpseId });
        Assert.Equal(ItemScenarioWorld.MobCorpsePresentationKey, presentationKey);
        var lootQuantity = await connection.QuerySingleAsync<long>(
            """
            select coalesce(sum(item.quantity), 0)::bigint
            from corpse_sections section
            join item_instances item on item.container_id = section.container_id
            where section.corpse_id = @CorpseId and item.definition_id = 'fixture.ore';
            """,
            new { hotspot.CorpseId });
        Assert.Equal((long)hotspot.LootStacks * hotspot.QuantityPerStack, lootQuantity);
    }
}
