using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// A creature's corpse gives whole items only. The worker enforces that for the live corpses it
// keeps itself; these tests hold GameService to it for the durable ones, such as a boss's, where
// GameService is the only place that can.
public sealed class CreatureCorpseWholeItemsIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task ACreatureCorpseRefusesAPartOfAStackADepositAndAMoveAndGivesWholeItems()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var looter = await world.RegisterAsync("creature_looter");
        var corpseId = await CreateCreatureCorpseAsync(world, looter, 10, 5);
        var general = (await world.LoadCorpseSectionsAsync(corpseId)).GeneralContainerId;
        var looterInventory = (await world.LoadStateAsync(looter.Character.Id)).PermanentInventoryContainerId;
        var carried = await world.GrantAsync(looter.Character.Id, "fixture.ore", 3, looterInventory, 5);
        var stack = await world.LoadItemAsync(await world.FindItemAsync(general, 0));

        var part = await ExecuteAsync(world, new LootCorpsePartialStackCommand(
            looter.Character.Id,
            corpseId,
            stack.ItemInstanceId,
            stack.Revision,
            4,
            looterInventory,
            await world.LoadContainerRevisionAsync(looterInventory),
            0,
            null,
            null));
        var deposit = await ExecuteAsync(world, new DepositCorpseItemCommand(
            looter.Character.Id,
            corpseId,
            carried,
            (await world.LoadItemAsync(carried)).Revision,
            general,
            await world.LoadContainerRevisionAsync(general),
            7,
            null,
            null));
        var move = await ExecuteAsync(world, new MoveCorpseItemCommand(
            looter.Character.Id,
            corpseId,
            stack.ItemInstanceId,
            stack.Revision,
            general,
            await world.LoadContainerRevisionAsync(general),
            9,
            null,
            null));

        AssertWholeItemsOnly(part);
        AssertWholeItemsOnly(deposit);
        AssertWholeItemsOnly(move);
        Assert.Equal((10, general, 0), Location(await world.LoadItemAsync(stack.ItemInstanceId)));
        Assert.Equal((3, looterInventory, 5), Location(await world.LoadItemAsync(carried)));

        var whole = await ExecuteAsync(world, new LootCorpseItemCommand(
            looter.Character.Id,
            corpseId,
            stack.ItemInstanceId,
            stack.Revision,
            looterInventory,
            await world.LoadContainerRevisionAsync(looterInventory),
            0,
            null,
            null));

        ItemScenarioWorld.AssertCommitted(whole);
        Assert.Equal((10, looterInventory, 0), Location(await world.LoadItemAsync(stack.ItemInstanceId)));
    }

    [PostgresIntegrationFact]
    public async Task LootAllLeavesAStackOfACreatureCorpseThatDoesNotFitWhole()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var looter = await world.RegisterAsync("creature_heavy_looter");
        var looterInventory = (await world.LoadStateAsync(looter.Character.Id)).PermanentInventoryContainerId;
        // 40 ore weigh 240 of the 280 that capacity 200 allows at the hard cap, so 6 more fit;
        // CorpseLootAllIntegrationTests shows that a player's corpse then gives those 6.
        var carriedOre = await world.GrantAsync(looter.Character.Id, "fixture.ore", 40, looterInventory, 0);
        var corpseId = await CreateCreatureCorpseAsync(world, looter, 20);
        var general = (await world.LoadCorpseSectionsAsync(corpseId)).GeneralContainerId;
        var corpseOre = await world.FindItemAsync(general, 0);
        var revision = await world.Context.ExecuteScalarIntAsync(
            $"select revision::integer from corpses where id = '{corpseId}';");

        var result = await ExecuteAsync(
            world,
            new LootAllCorpseItemsCommand(looter.Character.Id, corpseId, revision));

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.InventoryFull, result.Error!.Code);
        Assert.Equal(40, (await world.LoadItemAsync(carriedOre)).Quantity);
        Assert.Equal(20, (await world.LoadItemAsync(corpseOre)).Quantity);
    }

    private static void AssertWholeItemsOnly(ItemTransactionResult result)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.CorpseWholeItemsOnly, result.Error!.Code);
    }

    private static (int Quantity, Guid? ContainerId, int? SlotIndex) Location(ItemScenarioWorld.ItemRow item)
    {
        return (item.Quantity, item.ContainerId, item.ContainerSlotIndex);
    }

    private static Task<ItemTransactionResult> ExecuteAsync<TCommand>(ItemScenarioWorld world, TCommand command)
        where TCommand : notnull
    {
        return world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<TCommand>(Guid.NewGuid(), ItemTransactionActor.ForSystem(), command),
            CancellationToken.None);
    }

    // A durable creature corpse with one stack of ore per quantity, created as the worker does.
    private static async Task<Guid> CreateCreatureCorpseAsync(
        ItemScenarioWorld world,
        IntegrationPlayer looter,
        params int[] oreStacks)
    {
        // A join registers the worker runtime whose lease the corpse command checks.
        await world.JoinAsync(looter);
        var corpseId = Guid.NewGuid();
        var created = await world.Context.CorpseService.CreatePersistentMobCorpseAsync(
            ItemScenarioWorld.WorkerId,
            new CreatePersistentMobCorpseRequest(
                Guid.NewGuid(),
                corpseId,
                ItemScenarioWorld.WorkerId,
                ItemScenarioWorld.WorkerRuntimeId,
                ItemScenarioWorld.ShardId,
                "mob.feral_alpha",
                1.5d,
                0d,
                -2d,
                0d,
                0d,
                0d,
                1d,
                ItemScenarioWorld.MobCorpsePresentationKey,
                600d,
                oreStacks.Select(quantity => new MobCorpseLootEntryRequest(Guid.NewGuid(), "fixture.ore", quantity)).ToArray()),
            CancellationToken.None);
        Assert.True(created.Succeeded, created.Error?.Message);
        return corpseId;
    }
}
