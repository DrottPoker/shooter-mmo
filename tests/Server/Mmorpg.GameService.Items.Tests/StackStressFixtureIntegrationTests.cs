using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items.StackStress;

using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// The loot hotspot of the stack stress workloads is the corpse of the fixture's own character,
// made by the death transaction of every player, so the bots may deposit into it, move inside
// it, and take parts of its stacks, which a creature's corpse refuses.
public sealed class StackStressFixtureIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task TheLootHotspotFixtureCreatesAPlayerCorpseWithItsLootAndRefusesALongerRun()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var fixture = await world.RegisterAsync("stress_hotspot");
        var service = KitService(context);

        var result = await service.CreateLootHotspotAsync(
            fixture.Registration.AccountId,
            new StackStressLootHotspotRequest(
                "hotspot1",
                fixture.Character.Id,
                ItemScenarioWorld.ShardId,
                0d,
                0d,
                0d,
                240d),
            CancellationToken.None);
        var tooLong = await service.CreateLootHotspotAsync(
            fixture.Registration.AccountId,
            new StackStressLootHotspotRequest("hotspot1", fixture.Character.Id, ItemScenarioWorld.ShardId, 0d, 0d, 0d, 301d),
            CancellationToken.None);

        Assert.False(tooLong.Succeeded);
        Assert.Contains("lasts 300 seconds", tooLong.Error!.Message, StringComparison.Ordinal);
        Assert.True(result.Succeeded, result.Error?.Message);
        var hotspot = result.Value!;
        Assert.Equal("fixture.ore", hotspot.LootDefinitionId);
        Assert.True(hotspot.LootStacks > 0);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var corpse = await connection.QuerySingleAsync<(string SourceType, string PresentationKey, double LifetimeSeconds)>(
            """
            select source_type, presentation_key, extract(epoch from expires_at - now())::double precision
            from corpses
            where id = @CorpseId;
            """,
            new { hotspot.CorpseId });
        Assert.Equal(("player", ItemScenarioWorld.CorpsePresentationKey), (corpse.SourceType, corpse.PresentationKey));
        Assert.InRange(corpse.LifetimeSeconds, 280d, 300d);
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

    // A playing load bot receives the kit of its role through the same item transactions as
    // every other grant, into the first permanent inventory slots, and equips it itself.
    [PostgresIntegrationFact]
    public async Task TheKitFixtureGrantsTheConfiguredKitIntoANewCharactersInventory()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("stress_kit");
        var service = KitService(context);

        var result = await service.SeedKitAsync(
            player.Account.AccountId,
            player.Character.Id,
            new StackStressKitRequest("bow", "kit1"),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var kit = result.Value!;
        Assert.Equal("bow", kit.Kit);
        var granted = kit.Inventory.PermanentInventory.Slots
            .Where(slot => slot.Item is not null)
            .Select(slot => (slot.SlotIndex, slot.Item!.DefinitionId, slot.Item.Quantity))
            .ToArray();
        Assert.Equal([(0, "fixture.bow", 1), (1, "fixture.arrow", 40)], granted);
        Assert.All(kit.Inventory.Equipment, slot => Assert.Null(slot.Item));
        Assert.Equal(
            kit.Inventory.ItemStateRevision,
            (await world.LoadStateAsync(player.Character.Id)).Revision);
    }

    [PostgresIntegrationFact]
    public async Task TheKitFixtureRefusesAnUnknownKitAndACharacterThatHasItems()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("stress_kit_refused");
        var service = KitService(context);

        var unknown = await service.SeedKitAsync(
            player.Account.AccountId,
            player.Character.Id,
            new StackStressKitRequest("staff", "kit2"),
            CancellationToken.None);
        Assert.False(unknown.Succeeded);
        Assert.Equal("stack_stress_fixture_invalid", unknown.Error!.Code);

        var first = await service.SeedKitAsync(
            player.Account.AccountId,
            player.Character.Id,
            new StackStressKitRequest("sword", "kit2"),
            CancellationToken.None);
        Assert.True(first.Succeeded, first.Error?.Message);
        var second = await service.SeedKitAsync(
            player.Account.AccountId,
            player.Character.Id,
            new StackStressKitRequest("sword", "kit2"),
            CancellationToken.None);
        Assert.False(second.Succeeded);
        Assert.Equal("stack_stress_fixture_not_empty", second.Error!.Code);
    }

    private static StackStressFixtureService KitService(PostgresIntegrationTestContext context)
    {
        return new StackStressFixtureService(
            context.ItemQueryService,
            context.ItemTransactionService,
            context.CorpseService,
            new StackStressFixtureOptions(true, "stack-stress-test-fixture-secret-0000")
            {
                InventoryDefinitionId = "fixture.potion",
                LootDefinitionId = "fixture.ore",
                Kits = new Dictionary<string, IReadOnlyList<StackStressFixtureOptions.KitGrant>>(StringComparer.Ordinal)
                {
                    ["sword"] = [new("fixture.sword", 1)],
                    ["bow"] = [new("fixture.bow", 1), new("fixture.arrow", 40)]
                }
            });
    }
}
