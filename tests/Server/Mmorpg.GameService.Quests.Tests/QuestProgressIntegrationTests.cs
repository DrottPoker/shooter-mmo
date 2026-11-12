using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;
using static Mmorpg.GameService.Quests.Tests.QuestScenario;

namespace Mmorpg.GameService.Quests.Tests;

// Objective progress and the turn-in through the item transaction kernel: credited kills,
// talking, delivering, collecting, and the rewards, with the fixture quests of QuestScenario.
// GatheringQuestCreditIntegrationTests in Mmorpg.GameService.Economy.Tests covers gathering
// yields.
public sealed class QuestProgressIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task KillsCountForTheNamedMobsUpToTheObjectiveCount()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (world, player, session) = await JoinAsync(context, "quest_hunter");
        var characterId = player.Character.Id;
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira));

        var first = await KillAsync(context, session, characterId, "mob.feral_wolf");
        var other = await KillAsync(context, session, characterId, "mob.grey_wolf");
        var second = await KillAsync(context, session, characterId, "mob.feral_wolf");
        var third = await KillAsync(context, session, characterId, "mob.feral_wolf");

        Assert.Equal(1, Progress(first, Wolves, "wolves"));
        Assert.Empty(QuestLogSection.Read(other)!.Quests);
        Assert.Equal(2, Progress(second, Wolves, "wolves"));
        Assert.Empty(QuestLogSection.Read(third)!.Quests);
        Assert.Equal(QuestLogSection.Read(second)!.Revision, QuestLogSection.Read(third)!.Revision);
        // The only step is the last, so the complete quest waits for its turn-in.
        Assert.Equal("hunt", Assert.Single((await ReadQuestLogAsync(context, characterId)).ActiveQuests).StepId);
        Assert.Empty(first.ItemChanges ?? []);
        Assert.NotNull(world);
    }

    // An active quest that the catalog does not hold, such as one a manual database change left
    // behind, gets no credit and never stops the credit of the others.
    [PostgresIntegrationFact]
    public async Task AQuestMissingFromTheCatalogNeverBlocksTheCreditOfTheOthers()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "quest_legacy_hunter");
        var characterId = player.Character.Id;
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira));
        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                insert into quest_definitions (id, structural_fingerprint) values ('quest.fixture.legacy', @Fingerprint);
                insert into character_active_quests (character_id, quest_id, step_id) values (@CharacterId, 'quest.fixture.legacy', 'hunt');
                """,
                new { Fingerprint = new string('a', 64), CharacterId = characterId });
        }

        var credited = await KillAsync(context, session, characterId, "mob.feral_wolf");

        Assert.Equal(1, Progress(credited, Wolves, "wolves"));
    }

    // A kill credit carries every tag an actor may have, and no more.
    [PostgresIntegrationFact]
    public async Task AKillCreditTakesEveryTagAnActorMayCarry()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "quest_tagged_hunter");
        var characterId = player.Character.Id;
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira));
        var tags = Enumerable.Range(0, WorldActorCompiler.MaximumTagsPerActor).Select(index => $"tag{index}").ToArray();

        var fullyTagged = await ExecuteAsync(context, session, new CreditQuestKillCommand(characterId, "mob.feral_wolf", tags), null);
        var overTagged = await ExecuteAsync(
            context,
            session,
            new CreditQuestKillCommand(characterId, "mob.feral_wolf", [.. tags, "one_more"]),
            null);

        Assert.Equal(1, Progress(fullyTagged, Wolves, "wolves"));
        Assert.Equal(ItemTransactionErrorCodes.SimulationOperationInvalid, overTagged.Error!.Code);
    }

    [PostgresIntegrationFact]
    public async Task TalkingAndDeliveringCompleteTheirObjectivesAndTheStepMovesOn()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (world, player, session) = await JoinErrandAsync(context, "quest_courier");
        var characterId = player.Character.Id;
        await SetProgressAsync(context, characterId, Errand, "ore", 2);

        var missing = await ExecuteAsync(context, session, new DeliverQuestItemsCommand(characterId, Errand, "hand_over"), Mira);
        var wrongNpc = await ExecuteAsync(context, session, new TalkToQuestNpcCommand(characterId, Errand, "report"), Guard);
        var wrongKind = await ExecuteAsync(context, session, new TalkToQuestNpcCommand(characterId, Errand, "hand_over"), Mira);
        var talked = await ExecuteAsync(context, session, new TalkToQuestNpcCommand(characterId, Errand, "report"), Mira);
        var twice = await ExecuteAsync(context, session, new TalkToQuestNpcCommand(characterId, Errand, "report"), Mira);
        var state = await world.LoadStateAsync(characterId);
        var ore = await world.GrantAsync(characterId, Ore, 1, state.PermanentInventoryContainerId, 3);
        var delivered = await ExecuteAsync(context, session, new DeliverQuestItemsCommand(characterId, Errand, "hand_over"), Mira);
        var stale = await ExecuteAsync(context, session, new TalkToQuestNpcCommand(characterId, Errand, "report"), Mira);

        Assert.Equal(QuestTransactionErrorCodes.QuestItemsMissing, missing.Error!.Code);
        Assert.Equal(QuestTransactionErrorCodes.QuestNpcAccessRequired, wrongNpc.Error!.Code);
        Assert.Equal(QuestTransactionErrorCodes.QuestObjectiveInvalid, wrongKind.Error!.Code);
        Assert.Equal(1, Progress(talked, Errand, "report"));
        Assert.Equal(QuestTransactionErrorCodes.QuestObjectiveComplete, twice.Error!.Code);
        ItemScenarioWorld.AssertCommitted(delivered);
        Assert.Contains(delivered.ItemChanges!, change => change.ItemInstanceId == ore && change.Removed);
        var quest = Assert.Single(QuestLogSection.Read(delivered)!.Quests);
        Assert.Equal("return", quest.StepId);
        Assert.Empty(quest.Objectives);
        Assert.Equal(QuestTransactionErrorCodes.QuestObjectiveInvalid, stale.Error!.Code);
    }

    [PostgresIntegrationFact]
    public async Task TurningInTakesTheCollectedItemsRemovesTheRestOfTheLineageAndPaysTheGold()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (world, player, session) = await JoinErrandAsync(context, "quest_returner");
        var characterId = player.Character.Id;
        await AdvanceAsync(context, characterId, Errand, "return", []);
        var state = await world.LoadStateAsync(characterId);
        var carried = Assert.Single(await LineageItemsAsync(context, characterId, Errand));
        var banked = await AddSpareLineageItemAsync(world, state.BankContainerId, Errand);
        var before = await CurrencyAsync(context, characterId);

        var turnedIn = await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, Errand, null), Mira);

        ItemScenarioWorld.AssertCommitted(turnedIn);
        var quest = Assert.Single(QuestLogSection.Read(turnedIn)!.Quests);
        Assert.Equal(((string?)null, 1), (quest.StepId, quest.CompletionCount));
        Assert.Empty(await LineageItemsAsync(context, characterId, Errand));
        Assert.Null(await DestructionReasonAsync(context, carried.ItemInstanceId));
        Assert.Equal("quest_completed", await DestructionReasonAsync(context, banked));
        Assert.Equal(before + 5, await CurrencyAsync(context, characterId));
        var ledger = await LastLedgerRowAsync(context, characterId);
        Assert.Equal((CurrencySourceKinds.QuestReward, Errand, 5L, turnedIn.OperationId), ledger);
        Assert.Equal(before + 5, Assert.Single(turnedIn.CharacterRevisions).Currency);
    }

    [PostgresIntegrationFact]
    public async Task TheTurnInNeedsAReadyQuestAndOneOfItsRewardChoices()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (world, player, session) = await JoinAsync(context, "quest_chooser");
        var characterId = player.Character.Id;
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira));
        await SetProgressAsync(context, characterId, Wolves, "wolves", 1);

        var notReady = await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, Wolves, "fixture.sword"), Mira);
        await SetProgressAsync(context, characterId, Wolves, "wolves", 2);
        var wrongNpc = await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, Wolves, "fixture.sword"), Guard);
        var noChoice = await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, Wolves, null), Mira);
        var unknownChoice = await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, Wolves, "fixture.ring"), Mira);
        var turnedIn = await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, Wolves, "fixture.shield"), Mira);

        Assert.Equal(QuestTransactionErrorCodes.QuestNotReady, notReady.Error!.Code);
        Assert.Equal(QuestTransactionErrorCodes.QuestNpcAccessRequired, wrongNpc.Error!.Code);
        Assert.Equal(QuestTransactionErrorCodes.QuestRewardChoiceRequired, noChoice.Error!.Code);
        Assert.Equal(QuestTransactionErrorCodes.QuestRewardChoiceInvalid, unknownChoice.Error!.Code);
        ItemScenarioWorld.AssertCommitted(turnedIn);
        Assert.Single(await InventoryStacksAsync(context, world, characterId, "fixture.potion"));
        Assert.Single(await InventoryStacksAsync(context, world, characterId, "fixture.shield"));
        Assert.Empty(await InventoryStacksAsync(context, world, characterId, "fixture.sword"));
        Assert.Equal(
            QuestTransactionErrorCodes.QuestAlreadyCompleted,
            (await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira)).Error!.Code);
    }

    [PostgresIntegrationFact]
    public async Task ARewardWithoutRoomRejectsTheWholeTurnIn()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (world, player, session) = await JoinAsync(context, "quest_overloaded");
        var characterId = player.Character.Id;
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira));
        await SetProgressAsync(context, characterId, Wolves, "wolves", 2);
        await FillPermanentInventoryAsync(context, world, characterId);
        var before = await CurrencyAsync(context, characterId);

        var result = await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, Wolves, "fixture.sword"), Mira);

        Assert.Equal(ItemTransactionErrorCodes.InventoryFull, result.Error!.Code);
        Assert.Equal(before, await CurrencyAsync(context, characterId));
        Assert.Equal(Wolves, Assert.Single((await ReadQuestLogAsync(context, characterId)).ActiveQuests).QuestId);
    }

    [PostgresIntegrationFact]
    public async Task OrdinaryItemsAreCollectedFromCarriedStacksAndARepeatableQuestStartsAgain()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (world, player, session) = await JoinAsync(context, "quest_ore_runner");
        var characterId = player.Character.Id;
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, OreRun), Mira));
        var state = await world.LoadStateAsync(characterId);
        await world.GrantAsync(characterId, Ore, 2, state.PermanentInventoryContainerId, 0);
        await world.GrantAsync(characterId, Ore, 5, state.BankContainerId, 0);

        // Banked items never count.
        var short1 = await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, OreRun, null), Mira);
        await world.GrantAsync(characterId, Ore, 2, state.PermanentInventoryContainerId, 1);
        var turnedIn = await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, OreRun, null), Mira);
        var again = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, OreRun), Mira);

        Assert.Equal(QuestTransactionErrorCodes.QuestNotReady, short1.Error!.Code);
        ItemScenarioWorld.AssertCommitted(turnedIn);
        Assert.Equal([(1, 1)], await InventoryStacksAsync(context, world, characterId, Ore));
        ItemScenarioWorld.AssertCommitted(again);
        var quest = Assert.Single(QuestLogSection.Read(again)!.Quests);
        Assert.Equal(("deliver", 1), (quest.StepId, quest.CompletionCount));
    }

    [PostgresIntegrationFact]
    public async Task TheWorkerRouteRunsProgressAndTurnInOperations()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "quest_route_progress");
        var characterId = player.Character.Id;
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira));

        var kill = await RouteAsync(context, session, "credit_quest_kill", null, null, mobActorId: "mob.feral_wolf", mobTags: []);
        var untagged = await RouteAsync(context, session, "credit_quest_kill", null, null, mobActorId: "mob.feral_wolf");
        var noObjective = await RouteAsync(context, session, "talk_to_quest_npc", Wolves, Mira);
        await RouteAsync(context, session, "credit_quest_kill", null, null, mobActorId: "mob.feral_wolf", mobTags: ["wolf"]);
        var turnedIn = await RouteAsync(context, session, "turn_in_quest", Wolves, Mira, rewardChoiceDefinitionId: "fixture.bow");

        Assert.True(kill.Succeeded, kill.Error?.Message);
        Assert.Equal(1, Assert.Single(QuestLogOf(kill.Value!)!.Quests).Objectives.Single().Progress);
        Assert.Equal(400, untagged.StatusCode);
        Assert.Equal(400, noObjective.StatusCode);
        Assert.True(turnedIn.Succeeded, turnedIn.Error?.Message);
        Assert.Equal(1, Assert.Single(QuestLogOf(turnedIn.Value!)!.Quests).CompletionCount);
        Assert.NotNull(turnedIn.Value!.CharacterState);
    }

    private static async Task<(ItemScenarioWorld World, IntegrationPlayer Player, ConsumedSimulationJoinTicketResponse Session)> JoinAsync(
        PostgresIntegrationTestContext context,
        string name)
    {
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync(name);
        return (world, player, await world.JoinAsync(player));
    }

    // A character at the errand's first step, with its letter.
    private static async Task<(ItemScenarioWorld World, IntegrationPlayer Player, ConsumedSimulationJoinTicketResponse Session)> JoinErrandAsync(
        PostgresIntegrationTestContext context,
        string name)
    {
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync(name);
        await CompleteAsync(context, player.Character.Id, Wolves, "hunt");
        var session = await world.JoinAsync(player);
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(
            context,
            session,
            new AcceptQuestCommand(player.Character.Id, Errand),
            Mira));
        return (world, player, session);
    }

    private static Task<ItemTransactionResult> KillAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        Guid characterId,
        string mobActorId)
    {
        return ExecuteAsync(context, session, new CreditQuestKillCommand(characterId, mobActorId, ["wolf"]), null);
    }

    private static int Progress(ItemTransactionResult result, string questId, string objectiveId)
    {
        ItemScenarioWorld.AssertCommitted(result);
        return QuestLogSection.Read(result)!.Quests
            .Single(quest => quest.QuestId == questId)
            .Objectives
            .Single(objective => objective.ObjectiveId == objectiveId)
            .Progress;
    }

    // A spare item with the quest's lineage, such as a quest drop the turn-in does not need. The
    // grant command never grants a lineage item twice, so the test writes it as the item code does.
    private static async Task<Guid> AddSpareLineageItemAsync(ItemScenarioWorld world, Guid containerId, string questId)
    {
        var itemInstanceId = Guid.NewGuid();
        await world.ExecuteSqlAsync(
            """
            insert into item_instances (id, definition_id, quantity, container_id, container_slot_index)
            values (@ItemInstanceId, @DefinitionId, 1, @ContainerId, 0);

            insert into item_instance_policies (id, item_instance_id, policy_kind, source_kind, source_id)
            values (@PolicyId, @ItemInstanceId, @PolicyKind, 'quest_grant', @QuestId);
            """,
            new
            {
                ItemInstanceId = itemInstanceId,
                DefinitionId = Letter,
                ContainerId = containerId,
                PolicyId = Guid.NewGuid(),
                PolicyKind = ItemPolicyIds.ProtectedOnDeath,
                QuestId = questId
            });
        return itemInstanceId;
    }

    private static async Task<long> CurrencyAsync(PostgresIntegrationTestContext context, Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<long>(
            "select currency from characters where id = @CharacterId;",
            new { CharacterId = characterId });
    }

    private static async Task<(string SourceKind, string SourceId, long Delta, Guid? ItemOperationId)> LastLedgerRowAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<(string, string, long, Guid?)>(
            """
            select source_kind, source_id, delta, item_operation_id
            from character_currency_ledger
            where character_id = @CharacterId
            order by id desc
            limit 1;
            """,
            new { CharacterId = characterId });
    }
}
