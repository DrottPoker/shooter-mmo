using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items.Tests.ItemTransactions;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Testing;
using Mmorpg.Server.Common.Identity;
using Mmorpg.Testing;
using static Mmorpg.GameService.Items.Tests.QuestScenario;

namespace Mmorpg.GameService.Items.Tests;

// Quest drops: the trophies that Feral Wolves drop for quest.fixture.trophies, granted from a live
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
        var trophies = await LineageItemsAsync(context, characterId, Trophies);
        Assert.Equal(2, trophies.Count);
        Assert.All(trophies, trophy => Assert.Equal((Trophy, 1, inventoryId), (trophy.DefinitionId, trophy.Quantity, trophy.ContainerId)));
    }

    [PostgresIntegrationFact]
    public async Task LiveLootAllTakesTheTrophiesTheQuestStillNeedsAndLeavesTheRest()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_drop_gatherer");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Trophies), Mira));
        var ore = new MobLootGrantEntry(Guid.NewGuid(), Ore, 3);
        MobLootGrantEntry[] trophies =
        [
            new(Guid.NewGuid(), Trophy, 1, Trophies),
            new(Guid.NewGuid(), Trophy, 1, Trophies),
            new(Guid.NewGuid(), Trophy, 1, Trophies)
        ];

        var looted = await GrantLootAllAsync(world, session, characterId, [ore, .. trophies]);
        var nothingNeeded = await GrantLootAllAsync(world, session, characterId, [trophies[2]]);

        ItemScenarioWorld.AssertCommitted(looted);
        Assert.Equal(2, (await LineageItemsAsync(context, characterId, Trophies)).Count);
        Assert.Equal(
            [ore.GrantId, trophies[0].GrantId, trophies[1].GrantId],
            await GrantedAsync(world, [ore.GrantId, .. trophies.Select(trophy => trophy.GrantId)]));
        Assert.Equal(ItemTransactionErrorCodes.QuestItemNotNeeded, nothingNeeded.Error!.Code);
    }

    [PostgresIntegrationFact]
    public async Task ADurableMobCorpseKeepsOnlyTheTrophyItsOwnerNeedsAndLetsTheOwnerLootIt()
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
        var firstTrophy = new MobCorpseLootEntryRequest(Guid.NewGuid(), Trophy, 1, Trophies);

        ItemScenarioWorld.AssertCommitted(await CreateCorpseAsync(
            world,
            firstCorpseId,
            characterId,
            [new MobCorpseLootEntryRequest(Guid.NewGuid(), Ore, 2), firstTrophy]));
        var unneeded = await CreateCorpseAsync(world, Guid.NewGuid(), stranger.Character.Id, [firstTrophy with { GrantId = Guid.NewGuid() }]);
        var ownerless = await CreateCorpseAsync(world, Guid.NewGuid(), null, [firstTrophy with { GrantId = Guid.NewGuid() }]);

        var firstTrophyId = ScopedGuid.Create("mob-loot-item", firstTrophy.GrantId);
        Assert.Equal(Trophies, await LineageOfAsync(world, firstTrophyId));
        Assert.Equal(ItemTransactionErrorCodes.MobCorpseEmpty, unneeded.Error!.Code);
        Assert.Equal(ItemTransactionErrorCodes.MobCorpseInvalid, ownerless.Error!.Code);
        ItemScenarioWorld.AssertCommitted(await LootAsync(world, characterId, firstCorpseId, firstTrophyId, inventoryId, 0));
        Assert.Equal(firstTrophyId, Assert.Single(await LineageItemsAsync(context, characterId, Trophies)).ItemInstanceId);

        // The second corpse keeps a trophy the quest still needs, which a live drop then fills.
        var secondCorpseId = Guid.NewGuid();
        var secondTrophy = new MobCorpseLootEntryRequest(Guid.NewGuid(), Trophy, 1, Trophies);
        ItemScenarioWorld.AssertCommitted(await CreateCorpseAsync(
            world,
            secondCorpseId,
            characterId,
            [new MobCorpseLootEntryRequest(Guid.NewGuid(), Ore, 1), secondTrophy]));
        ItemScenarioWorld.AssertCommitted(await GrantAsync(world, session, characterId, Wolf, Trophies, inventoryId, 1));
        var secondTrophyId = ScopedGuid.Create("mob-loot-item", secondTrophy.GrantId);

        var notNeeded = await LootAsync(world, characterId, secondCorpseId, secondTrophyId, inventoryId, 2);
        var lootAll = await LootAllAsync(world, characterId, secondCorpseId);

        Assert.Equal(ItemTransactionErrorCodes.QuestItemNotNeeded, notNeeded.Error!.Code);
        ItemScenarioWorld.AssertCommitted(lootAll);
        Assert.Equal(2, (await LineageItemsAsync(context, characterId, Trophies)).Count);
        Assert.NotEqual(inventoryId, (await world.LoadItemAsync(secondTrophyId)).ContainerId);
    }

    // Two trophies of one corpse share what the quest still needs, at creation and in Loot all.
    [PostgresIntegrationFact]
    public async Task TrophiesOfOneDurableCorpseShareWhatTheQuestStillNeeds()
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
        MobCorpseLootEntryRequest[] trophies =
        [
            new(Guid.NewGuid(), Trophy, 1, Trophies),
            new(Guid.NewGuid(), Trophy, 1, Trophies),
            new(Guid.NewGuid(), Trophy, 1, Trophies)
        ];

        ItemScenarioWorld.AssertCommitted(await CreateCorpseAsync(world, corpseId, characterId, trophies));
        Assert.Equal(2, await CountCorpseItemsAsync(world, corpseId));
        ItemScenarioWorld.AssertCommitted(await GrantAsync(world, session, characterId, Wolf, Trophies, inventoryId, 0));
        ItemScenarioWorld.AssertCommitted(await LootAllAsync(world, characterId, corpseId));

        Assert.Equal(2, (await LineageItemsAsync(context, characterId, Trophies)).Count);
        Assert.Equal(1, await CountCorpseItemsAsync(world, corpseId));
    }

    // A corpse almost full of the Mob's own loot keeps the trophy the owner needs in its last
    // entry: a trophy that no active quest needs does not take that place, and a second needed
    // trophy finds no room.
    [PostgresIntegrationFact]
    public async Task NeededQuestDropsFillTheRoomThatTheMobsOwnLootLeaves()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_drop_crowd");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Trophies), Mira));
        var corpseId = Guid.NewGuid();
        MobCorpseLootEntryRequest[] loot =
        [
            .. Enumerable.Range(0, 23).Select(_ => new MobCorpseLootEntryRequest(Guid.NewGuid(), Ore, 1)),
            new(Guid.NewGuid(), Trophy, 1, "quest.fixture.wolves"),
            new(Guid.NewGuid(), Trophy, 1, Trophies),
            new(Guid.NewGuid(), Trophy, 1, Trophies)
        ];

        var created = await CreateCorpseAsync(world, corpseId, characterId, loot);

        ItemScenarioWorld.AssertCommitted(created);
        Assert.Equal(24, await CountCorpseItemsAsync(world, corpseId));
        Assert.Equal(1, await CountCorpseItemsAsync(world, corpseId, Trophy));
        Assert.Equal([new ItemTransactionQuestDrop(Trophies, Trophy, 1)], created.QuestDrops);
    }

    // The worker learns which quest drops a durable corpse kept, so it can tell the owner, also
    // when it asks again with the same operation; a corpse without quest drops reports none.
    [PostgresIntegrationFact]
    public async Task TheWorkerLearnsWhichQuestDropsADurableCorpseKept()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_drop_reporter");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Trophies), Mira));
        var request = PersistentCorpseRequest(
            characterId,
            [
                new MobCorpseLootEntryRequest(Guid.NewGuid(), Ore, 2),
                new MobCorpseLootEntryRequest(Guid.NewGuid(), Trophy, 1, Trophies),
                new MobCorpseLootEntryRequest(Guid.NewGuid(), Trophy, 1, Trophies),
                new MobCorpseLootEntryRequest(Guid.NewGuid(), Trophy, 1, Trophies)
            ]);

        var created = await context.CorpseService.CreatePersistentMobCorpseAsync(WorkerId, request, CancellationToken.None);
        var replayed = await context.CorpseService.CreatePersistentMobCorpseAsync(WorkerId, request, CancellationToken.None);
        var oreOnly = await context.CorpseService.CreatePersistentMobCorpseAsync(
            WorkerId,
            PersistentCorpseRequest(characterId, [new MobCorpseLootEntryRequest(Guid.NewGuid(), Ore, 1)]),
            CancellationToken.None);

        // The trophies need two trophies, so the corpse keeps two of the three.
        SimulationQuestDrop[] kept = [new(Trophies, Trophy, 1), new(Trophies, Trophy, 1)];
        Assert.True(created.Succeeded, created.Error?.Message);
        Assert.Equal(kept, created.Value!.QuestDrops);
        Assert.True(replayed.Succeeded, replayed.Error?.Message);
        Assert.Equal(kept, replayed.Value!.QuestDrops);
        Assert.True(oreOnly.Succeeded, oreOnly.Error?.Message);
        Assert.Empty(oreOnly.Value!.QuestDrops);
    }

    // The worker's read before it lays out a live corpse: the drops that the owner's quests still
    // need, shared between drops of one item, with the checks of every worker read.
    [PostgresIntegrationFact]
    public async Task TheWorkerLearnsWhichRolledDropsTheOwnerStillNeeds()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_drop_reader");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        SimulationQuestDrop[] trophies =
        [
            new(Trophies, Trophy, 1),
            new(Trophies, Trophy, 1),
            new(Trophies, Trophy, 1)
        ];

        var beforeAccepting = await ResolveAsync(context, session, Wolf, trophies);
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Trophies), Mira));
        var accepted = await ResolveAsync(context, session, Wolf, trophies);
        var otherMob = await ResolveAsync(context, session, "mob.grey_wolf", trophies);
        ItemScenarioWorld.AssertCommitted(await GrantAsync(world, session, characterId, Wolf, Trophies, inventoryId, 0));
        var afterOne = await ResolveAsync(context, session, Wolf, trophies);
        var stranger = await ResolveAsync(context, session with { SimulationSessionToken = "not-the-token" }, Wolf, trophies);

        Assert.Empty(beforeAccepting.Value!.Drops);
        Assert.Equal([new(Trophies, Trophy, 1), new(Trophies, Trophy, 1)], accepted.Value!.Drops);
        Assert.Empty(otherMob.Value!.Drops);
        Assert.Equal([new(Trophies, Trophy, 1)], afterOne.Value!.Drops);
        Assert.Equal(("simulation_session_invalid", 401), (stranger.Error!.Code, stranger.StatusCode));
    }

    private static async Task<ServiceResult<SimulationQuestDropResponse>> ResolveAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        string sourceActorDefinitionId,
        IReadOnlyList<SimulationQuestDrop> drops)
    {
        await using var scope = context.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SimulationQuestDropService>().ResolveAsync(
            session.WorkerId,
            session.SimulationSessionId,
            new SimulationQuestDropRequest(
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                sourceActorDefinitionId,
                drops,
                PostgresIntegrationTestContext.FixtureQuestCatalogRevision),
            CancellationToken.None);
    }

    private static async Task<int> CountCorpseItemsAsync(ItemScenarioWorld world, Guid corpseId, string? definitionId = null)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            """
            select count(*)::integer
            from corpse_sections section
            join item_instances item on item.container_id = section.container_id
            where section.corpse_id = @CorpseId
                and (@DefinitionId::text is null or item.definition_id = @DefinitionId);
            """,
            new { CorpseId = corpseId, DefinitionId = definitionId });
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
                    Trophy,
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

    private static CreatePersistentMobCorpseRequest PersistentCorpseRequest(
        Guid ownerCharacterId,
        IReadOnlyList<MobCorpseLootEntryRequest> loot)
    {
        return new CreatePersistentMobCorpseRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
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
            loot)
        {
            OwnerCharacterId = ownerCharacterId,
            LootGroupId = Guid.NewGuid()
        };
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
