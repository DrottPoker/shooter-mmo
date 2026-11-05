using Dapper;
using Mmorpg.GameService.Items.Tests.ItemTransactions;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Testing;
using Mmorpg.Server.Common.Identity;
using Mmorpg.Testing;
using static Mmorpg.GameService.Items.Tests.QuestScenario;

namespace Mmorpg.GameService.Items.Tests;

// Quest drops: the letters that Feral Wolves drop for quest.fixture.trophies, granted from a live
// corpse or kept in a durable one with the quest's lineage, and only while the quest needs them.
public sealed class QuestDropIntegrationTests
{
    private const string Trophies = "quest.fixture.trophies";
    private const string Wolf = "mob.feral_wolf";
    private const string WorkerId = "local-simulation-worker-1";
    private const string WorkerRuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task ALiveQuestDropCarriesTheLineageAndStopsOnceTheQuestHasEnough()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_drop_looter");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        var notAccepted = await GrantAsync(world, session, characterId, Wolf, Trophies, inventoryId, 0);
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Trophies), Mira));

        var first = await GrantAsync(world, session, characterId, Wolf, Trophies, inventoryId, 0);
        var otherMob = await GrantAsync(world, session, characterId, "mob.grey_wolf", Trophies, inventoryId, 1);
        var second = await GrantAsync(world, session, characterId, Wolf, Trophies, inventoryId, 1);
        var third = await GrantAsync(world, session, characterId, Wolf, Trophies, inventoryId, 2);
        var withoutQuest = await GrantAsync(world, session, characterId, Wolf, null, inventoryId, 2);

        Assert.Equal(ItemTransactionErrorCodes.QuestItemNotNeeded, notAccepted.Error!.Code);
        ItemScenarioWorld.AssertCommitted(first);
        Assert.Equal(ItemTransactionErrorCodes.QuestItemNotNeeded, otherMob.Error!.Code);
        ItemScenarioWorld.AssertCommitted(second);
        Assert.Equal(ItemTransactionErrorCodes.QuestItemNotNeeded, third.Error!.Code);
        Assert.Equal(ItemTransactionErrorCodes.MobLootGrantInvalid, withoutQuest.Error!.Code);
        var letters = await LineageItemsAsync(context, characterId, Trophies);
        Assert.Equal(2, letters.Count);
        Assert.All(letters, letter => Assert.Equal((Letter, 1, inventoryId), (letter.DefinitionId, letter.Quantity, letter.ContainerId)));
    }

    [PostgresIntegrationFact]
    public async Task LiveLootAllTakesTheLettersTheQuestStillNeedsAndLeavesTheRest()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_drop_gatherer");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Trophies), Mira));
        var ore = new MobLootGrantEntry(Guid.NewGuid(), Ore, 3);
        MobLootGrantEntry[] letters =
        [
            new(Guid.NewGuid(), Letter, 1, Trophies),
            new(Guid.NewGuid(), Letter, 1, Trophies),
            new(Guid.NewGuid(), Letter, 1, Trophies)
        ];

        var looted = await GrantLootAllAsync(world, session, characterId, [ore, .. letters]);
        var nothingNeeded = await GrantLootAllAsync(world, session, characterId, [letters[2]]);

        ItemScenarioWorld.AssertCommitted(looted);
        Assert.Equal(2, (await LineageItemsAsync(context, characterId, Trophies)).Count);
        Assert.Equal(
            [ore.GrantId, letters[0].GrantId, letters[1].GrantId],
            await GrantedAsync(world, [ore.GrantId, .. letters.Select(letter => letter.GrantId)]));
        Assert.Equal(ItemTransactionErrorCodes.QuestItemNotNeeded, nothingNeeded.Error!.Code);
    }

    [PostgresIntegrationFact]
    public async Task ADurableMobCorpseKeepsOnlyTheLetterItsOwnerNeedsAndLetsTheOwnerLootIt()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_drop_owner");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        var stranger = await world.RegisterAsync("quest_drop_stranger");
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Trophies), Mira));
        var firstCorpseId = Guid.NewGuid();
        var firstLetter = new MobCorpseLootEntryRequest(Guid.NewGuid(), Letter, 1, Trophies);

        ItemScenarioWorld.AssertCommitted(await CreateCorpseAsync(
            world,
            firstCorpseId,
            characterId,
            [new MobCorpseLootEntryRequest(Guid.NewGuid(), Ore, 2), firstLetter]));
        var unneeded = await CreateCorpseAsync(world, Guid.NewGuid(), stranger.Character.Id, [firstLetter with { GrantId = Guid.NewGuid() }]);
        var ownerless = await CreateCorpseAsync(world, Guid.NewGuid(), null, [firstLetter with { GrantId = Guid.NewGuid() }]);

        var firstLetterId = ScopedGuid.Create("mob-loot-item", firstLetter.GrantId);
        Assert.Equal(Trophies, await LineageOfAsync(world, firstLetterId));
        Assert.Equal(ItemTransactionErrorCodes.MobCorpseEmpty, unneeded.Error!.Code);
        Assert.Equal(ItemTransactionErrorCodes.MobCorpseInvalid, ownerless.Error!.Code);
        ItemScenarioWorld.AssertCommitted(await LootAsync(world, characterId, firstCorpseId, firstLetterId, inventoryId, 0));
        Assert.Equal(firstLetterId, Assert.Single(await LineageItemsAsync(context, characterId, Trophies)).ItemInstanceId);

        // The second corpse keeps a letter the quest still needs, which a live drop then fills.
        var secondCorpseId = Guid.NewGuid();
        var secondLetter = new MobCorpseLootEntryRequest(Guid.NewGuid(), Letter, 1, Trophies);
        ItemScenarioWorld.AssertCommitted(await CreateCorpseAsync(
            world,
            secondCorpseId,
            characterId,
            [new MobCorpseLootEntryRequest(Guid.NewGuid(), Ore, 1), secondLetter]));
        ItemScenarioWorld.AssertCommitted(await GrantAsync(world, session, characterId, Wolf, Trophies, inventoryId, 1));
        var secondLetterId = ScopedGuid.Create("mob-loot-item", secondLetter.GrantId);

        var notNeeded = await LootAsync(world, characterId, secondCorpseId, secondLetterId, inventoryId, 2);
        var lootAll = await LootAllAsync(world, characterId, secondCorpseId);

        Assert.Equal(ItemTransactionErrorCodes.QuestItemNotNeeded, notNeeded.Error!.Code);
        ItemScenarioWorld.AssertCommitted(lootAll);
        Assert.Equal(2, (await LineageItemsAsync(context, characterId, Trophies)).Count);
        Assert.NotEqual(inventoryId, (await world.LoadItemAsync(secondLetterId)).ContainerId);
    }

    // Two letters of one corpse share what the quest still needs, at creation and in Loot all.
    [PostgresIntegrationFact]
    public async Task LettersOfOneDurableCorpseShareWhatTheQuestStillNeeds()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_drop_sharer");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Trophies), Mira));
        var corpseId = Guid.NewGuid();
        MobCorpseLootEntryRequest[] letters =
        [
            new(Guid.NewGuid(), Letter, 1, Trophies),
            new(Guid.NewGuid(), Letter, 1, Trophies),
            new(Guid.NewGuid(), Letter, 1, Trophies)
        ];

        ItemScenarioWorld.AssertCommitted(await CreateCorpseAsync(world, corpseId, characterId, letters));
        Assert.Equal(2, await CountCorpseItemsAsync(world, corpseId));
        ItemScenarioWorld.AssertCommitted(await GrantAsync(world, session, characterId, Wolf, Trophies, inventoryId, 0));
        ItemScenarioWorld.AssertCommitted(await LootAllAsync(world, characterId, corpseId));

        Assert.Equal(2, (await LineageItemsAsync(context, characterId, Trophies)).Count);
        Assert.Equal(1, await CountCorpseItemsAsync(world, corpseId));
    }

    private static async Task<int> CountCorpseItemsAsync(ItemScenarioWorld world, Guid corpseId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            """
            select count(*)::integer
            from corpse_sections section
            join item_instances item on item.container_id = section.container_id
            where section.corpse_id = @CorpseId;
            """,
            new { CorpseId = corpseId });
    }

    private static async Task<ItemTransactionResult> GrantAsync(
        ItemScenarioWorld world,
        ConsumedSimulationJoinTicketResponse session,
        Guid characterId,
        string sourceActorDefinitionId,
        string? questId,
        Guid containerId,
        int slotIndex)
    {
        var grantId = Guid.NewGuid();
        var state = await world.LoadStateAsync(characterId);
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantMobLootCommand>(
                grantId,
                ItemScenarioWorld.SimulationActor(session),
                new GrantMobLootCommand(
                    characterId,
                    state.Revision,
                    grantId,
                    Guid.NewGuid(),
                    sourceActorDefinitionId,
                    Letter,
                    1,
                    containerId,
                    await world.LoadContainerRevisionAsync(containerId),
                    slotIndex,
                    QuestId: questId)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> GrantLootAllAsync(
        ItemScenarioWorld world,
        ConsumedSimulationJoinTicketResponse session,
        Guid characterId,
        IReadOnlyList<MobLootGrantEntry> loot)
    {
        var state = await world.LoadStateAsync(characterId);
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantMobLootAllCommand>(
                Guid.NewGuid(),
                ItemScenarioWorld.SimulationActor(session),
                new GrantMobLootAllCommand(characterId, state.Revision, Guid.NewGuid(), Wolf, loot)),
            CancellationToken.None);
    }

    private static Task<ItemTransactionResult> CreateCorpseAsync(
        ItemScenarioWorld world,
        Guid corpseId,
        Guid? ownerCharacterId,
        IReadOnlyList<MobCorpseLootEntryRequest> loot)
    {
        return world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<CreatePersistentMobCorpseCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new CreatePersistentMobCorpseCommand(
                    corpseId,
                    WorkerId,
                    WorkerRuntimeId,
                    ShardId,
                    Wolf,
                    "Feral Wolf",
                    1.5d,
                    0d,
                    -2d,
                    0d,
                    0d,
                    0d,
                    1d,
                    "corpse.mob",
                    90d,
                    30,
                    loot,
                    ownerCharacterId,
                    ownerCharacterId is null ? null : Guid.NewGuid())),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> LootAsync(
        ItemScenarioWorld world,
        Guid looterId,
        Guid corpseId,
        Guid itemInstanceId,
        Guid containerId,
        int slotIndex)
    {
        var item = await world.LoadItemAsync(itemInstanceId);
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<LootCorpseItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new LootCorpseItemCommand(
                    looterId,
                    corpseId,
                    itemInstanceId,
                    item.Revision,
                    containerId,
                    await world.LoadContainerRevisionAsync(containerId),
                    slotIndex,
                    null,
                    null)),
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> LootAllAsync(ItemScenarioWorld world, Guid looterId, Guid corpseId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        var revision = await connection.QuerySingleAsync<long>(
            "select revision from corpses where id = @CorpseId;",
            new { CorpseId = corpseId });
        return await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<LootAllCorpseItemsCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new LootAllCorpseItemsCommand(looterId, corpseId, revision)),
            CancellationToken.None);
    }

    private static async Task<string?> LineageOfAsync(ItemScenarioWorld world, Guid itemInstanceId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<string>(
            """
            select source_id
            from item_instance_policies
            where item_instance_id = @ItemInstanceId
              and source_kind = 'quest_grant'
              and status = 'active';
            """,
            new { ItemInstanceId = itemInstanceId });
    }

    private static async Task<IReadOnlyList<Guid>> GrantedAsync(ItemScenarioWorld world, IReadOnlyList<Guid> grantIds)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        var granted = (await connection.QueryAsync<Guid>(
                "select grant_id from live_mob_loot_grants where grant_id = any(@GrantIds);",
                new { GrantIds = grantIds.ToArray() }))
            .ToHashSet();
        return grantIds.Where(granted.Contains).ToArray();
    }
}
