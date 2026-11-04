using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items.Tests.ItemTransactions;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Tests;

// Accepting and abandoning quests and granting their start items again through the item
// transaction kernel, with the fixture quest catalog: quest.fixture.wolves at Mira,
// quest.fixture.errand, which requires it and grants a fixture.letter, and the repeatable
// quest.fixture.ore_run.
public sealed class QuestCommandIntegrationTests
{
    private const string Mira = "npc.city_services";
    private const string Wolves = "quest.fixture.wolves";
    private const string Errand = "quest.fixture.errand";
    private const string OreRun = "quest.fixture.ore_run";
    private const string Letter = "fixture.letter";

    [PostgresIntegrationFact]
    public async Task AcceptingAQuestStartsItAtItsFirstStepAndReportsTheChange()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_accepter");
        var session = await world.JoinAsync(player);

        var result = await ExecuteAsync(context, session, new AcceptQuestCommand(player.Character.Id, Wolves), Mira);

        ItemScenarioWorld.AssertCommitted(result);
        var change = result.QuestLog!;
        Assert.Equal((player.Character.Id, 1L), (change.CharacterId, change.Revision));
        var quest = Assert.Single(change.Quests);
        Assert.Equal((Wolves, "hunt", 0), (quest.QuestId, quest.StepId, quest.CompletionCount));
        Assert.Equal([new ItemTransactionQuestObjectiveProgress("wolves", 0)], quest.Objectives);
        Assert.Empty(result.ItemChanges ?? []);
        var active = Assert.Single((await ReadQuestLogAsync(context, player.Character.Id)).ActiveQuests);
        Assert.Equal((Wolves, "hunt"), (active.QuestId, active.StepId));
    }

    [PostgresIntegrationFact]
    public async Task AcceptingGrantsTheStartItemsWithTheQuestsLineage()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_letter_carrier");
        var characterId = player.Character.Id;
        await CompleteAsync(context, characterId, Wolves, "hunt");
        var session = await world.JoinAsync(player);

        var result = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Errand), Mira);

        ItemScenarioWorld.AssertCommitted(result);
        var state = await world.LoadStateAsync(characterId);
        var letter = Assert.Single(await LineageItemsAsync(context, characterId, Errand));
        Assert.Equal((Letter, 1, state.PermanentInventoryContainerId), (letter.DefinitionId, letter.Quantity, letter.ContainerId));
        Assert.Contains(result.ItemChanges!, change => change.ItemInstanceId == letter.ItemInstanceId && !change.Removed);
        var quest = Assert.Single(result.QuestLog!.Quests);
        Assert.Equal(("work", 0), (quest.StepId, quest.CompletionCount));
        Assert.Equal(["hand_over", "ore", "report"], quest.Objectives.Select(objective => objective.ObjectiveId));
    }

    [PostgresIntegrationFact]
    public async Task AcceptingChecksTheGiverTheRequirementsAndWhatTheCharacterAlreadyDid()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_checker");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);

        var wrongNpc = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), "npc.city_guard");
        var missingRequirement = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Errand), Mira);
        var unknown = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, "quest.fixture.unknown"), Mira);
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira));
        var twice = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira);

        Assert.Equal(ItemTransactionErrorCodes.QuestNpcAccessRequired, wrongNpc.Error!.Code);
        Assert.Equal(ItemTransactionErrorCodes.QuestRequirementsNotMet, missingRequirement.Error!.Code);
        Assert.Equal(ItemTransactionErrorCodes.QuestNotFound, unknown.Error!.Code);
        Assert.Equal(ItemTransactionErrorCodes.QuestAlreadyActive, twice.Error!.Code);
        Assert.Empty(await LineageItemsAsync(context, characterId, Errand));

        // A completed quest is done for good, unless it is repeatable.
        await CompleteActiveAsync(context, characterId, Wolves);
        var again = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira);
        await CompleteAsync(context, characterId, OreRun, "deliver");
        var repeated = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, OreRun), Mira);

        Assert.Equal(ItemTransactionErrorCodes.QuestAlreadyCompleted, again.Error!.Code);
        ItemScenarioWorld.AssertCommitted(repeated);
        Assert.Equal(1, Assert.Single(repeated.QuestLog!.Quests).CompletionCount);
    }

    [PostgresIntegrationFact]
    public async Task AcceptingWithFullCarriedStorageChangesNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_full_pack");
        var characterId = player.Character.Id;
        await CompleteAsync(context, characterId, Wolves, "hunt");
        await FillPermanentInventoryAsync(context, world, characterId);
        var session = await world.JoinAsync(player);

        var result = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Errand), Mira);

        Assert.Equal(ItemTransactionErrorCodes.InventoryFull, result.Error!.Code);
        Assert.Empty(await LineageItemsAsync(context, characterId, Errand));
        Assert.DoesNotContain(
            (await ReadQuestLogAsync(context, characterId)).ActiveQuests,
            quest => quest.QuestId == Errand);
    }

    [PostgresIntegrationFact]
    public async Task AbandoningEndsTheQuestAndRemovesEveryItemWithItsLineage()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_abandoner");
        var characterId = player.Character.Id;
        await CompleteAsync(context, characterId, Wolves, "hunt");
        var session = await world.JoinAsync(player);
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Errand), Mira));
        var letter = Assert.Single(await LineageItemsAsync(context, characterId, Errand));

        // Abandoning works anywhere, away from every NPC.
        var abandoned = await ExecuteAsync(context, session, new AbandonQuestCommand(characterId, Errand), null);
        var twice = await ExecuteAsync(context, session, new AbandonQuestCommand(characterId, Errand), null);

        ItemScenarioWorld.AssertCommitted(abandoned);
        var quest = Assert.Single(abandoned.QuestLog!.Quests);
        Assert.Equal((Errand, (string?)null, 0), (quest.QuestId, quest.StepId, quest.CompletionCount));
        Assert.Contains(abandoned.ItemChanges!, change => change.ItemInstanceId == letter.ItemInstanceId && change.Removed);
        Assert.Empty(await LineageItemsAsync(context, characterId, Errand));
        Assert.Equal("quest_abandoned", await DestructionReasonAsync(context, letter.ItemInstanceId));
        Assert.Equal(ItemTransactionErrorCodes.QuestNotActive, twice.Error!.Code);
        var log = await ReadQuestLogAsync(context, characterId);
        Assert.Empty(log.ActiveQuests);
        Assert.Equal([Wolves], log.CompletedQuests.Select(completed => completed.QuestId));
    }

    [PostgresIntegrationFact]
    public async Task TheGiverGrantsMissingStartItemsAgainButNeverDuplicatesThem()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_regranter");
        var characterId = player.Character.Id;
        await CompleteAsync(context, characterId, Wolves, "hunt");
        var session = await world.JoinAsync(player);
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Errand), Mira));

        var present = await ExecuteAsync(context, session, new RegrantQuestItemsCommand(characterId, Errand), Mira);
        Assert.Single(await LineageItemsAsync(context, characterId, Errand));
        await RemoveLineageItemsAsync(context, characterId, Errand);
        var regranted = await ExecuteAsync(context, session, new RegrantQuestItemsCommand(characterId, Errand), Mira);
        var wrongNpc = await ExecuteAsync(context, session, new RegrantQuestItemsCommand(characterId, Errand), "npc.city_guard");
        var inactive = await ExecuteAsync(context, session, new RegrantQuestItemsCommand(characterId, OreRun), Mira);

        ItemScenarioWorld.AssertCommitted(present);
        Assert.Empty(present.ItemChanges ?? []);
        ItemScenarioWorld.AssertCommitted(regranted);
        var letter = Assert.Single(await LineageItemsAsync(context, characterId, Errand));
        Assert.Equal((Letter, 1), (letter.DefinitionId, letter.Quantity));
        Assert.Equal(ItemTransactionErrorCodes.QuestNpcAccessRequired, wrongNpc.Error!.Code);
        Assert.Equal(ItemTransactionErrorCodes.QuestNotActive, inactive.Error!.Code);
    }

    [PostgresIntegrationFact]
    public async Task ARetriedAcceptReplaysItsResultAndGrantsOnce()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_retrier");
        var characterId = player.Character.Id;
        await CompleteAsync(context, characterId, Wolves, "hunt");
        var session = await world.JoinAsync(player);
        var operationId = Guid.NewGuid();

        var first = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Errand), Mira, operationId);
        var retry = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Errand), Mira, operationId);

        ItemScenarioWorld.AssertCommitted(first);
        ItemScenarioWorld.AssertCommitted(retry);
        Assert.Equal(first.QuestLog!.Revision, retry.QuestLog!.Revision);
        Assert.Single(await LineageItemsAsync(context, characterId, Errand));
        Assert.Equal(first.QuestLog.Revision, (await ReadQuestLogAsync(context, characterId)).Revision);
    }

    [PostgresIntegrationFact]
    public async Task TheWorkerRouteRunsQuestOperationsAndMapsTheirRejections()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_route_user");
        var session = await world.JoinAsync(player);

        var accepted = await RouteAsync(context, session, "accept_quest", Wolves, Mira);
        var twice = await RouteAsync(context, session, "accept_quest", Wolves, Mira);
        var wrongNpc = await RouteAsync(context, session, "accept_quest", OreRun, "npc.city_guard");
        var unknown = await RouteAsync(context, session, "accept_quest", "quest.fixture.unknown", Mira);
        var unsupported = await RouteAsync(context, session, "complete_everything", Wolves, Mira);
        var abandoned = await RouteAsync(context, session, "abandon_quest", Wolves, null);

        Assert.True(accepted.Succeeded, accepted.Error?.Message);
        Assert.Equal(1, accepted.Value!.QuestLog!.Revision);
        Assert.Equal(player.Character.Id, accepted.Value.CharacterState!.CharacterId);
        Assert.Equal((409, ItemTransactionErrorCodes.QuestAlreadyActive), (twice.StatusCode, twice.Error!.Code));
        Assert.Equal((403, ItemTransactionErrorCodes.QuestNpcAccessRequired), (wrongNpc.StatusCode, wrongNpc.Error!.Code));
        Assert.Equal((404, ItemTransactionErrorCodes.QuestNotFound), (unknown.StatusCode, unknown.Error!.Code));
        Assert.Equal(400, unsupported.StatusCode);
        Assert.True(abandoned.Succeeded, abandoned.Error?.Message);
        Assert.Null(Assert.Single(abandoned.Value!.QuestLog!.Quests).StepId);
    }

    private static Task<ItemTransactionResult> ExecuteAsync<TCommand>(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        TCommand command,
        string? questNpcActorId,
        Guid? operationId = null)
        where TCommand : notnull
    {
        var request = new ItemTransactionRequest<TCommand>(
            operationId ?? Guid.NewGuid(),
            ItemScenarioWorld.SimulationActor(
                session,
                new ItemTransactionLiveAccess(false, false, false, QuestNpcActorId: questNpcActorId)),
            command);
        return request switch
        {
            ItemTransactionRequest<AcceptQuestCommand> accept => context.ItemTransactionService.ExecuteAsync(accept, CancellationToken.None),
            ItemTransactionRequest<AbandonQuestCommand> abandon => context.ItemTransactionService.ExecuteAsync(abandon, CancellationToken.None),
            ItemTransactionRequest<RegrantQuestItemsCommand> regrant => context.ItemTransactionService.ExecuteAsync(regrant, CancellationToken.None),
            _ => throw new ArgumentException("Not a quest command.", nameof(command))
        };
    }

    private static async Task<ServiceResult<SimulationQuestOperationResponse>> RouteAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        string operationKind,
        string questId,
        string? questNpcActorId)
    {
        await using var scope = context.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SimulationQuestMutationService>().ExecuteAsync(
            session.WorkerId,
            session.SimulationSessionId,
            new SimulationQuestOperationRequest(
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                operationKind,
                questId,
                questNpcActorId),
            CancellationToken.None);
    }

    private static async Task<CharacterQuestLogState> ReadQuestLogAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var log = await context.Services.GetRequiredService<ICharacterQuestLog>().ReadAsync(
            new PostgresTransactionScope(connection, transaction),
            characterId,
            CancellationToken.None);
        await transaction.CommitAsync();
        return log;
    }

    // Completed quests set up the requirements of later quests without playing them.
    private static async Task CompleteAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        string questId,
        string stepId)
    {
        await WriteQuestLogAsync(context, (questLog, scope) =>
            questLog.AcceptAsync(scope, characterId, questId, stepId, [], CancellationToken.None));
        await CompleteActiveAsync(context, characterId, questId);
    }

    private static Task CompleteActiveAsync(PostgresIntegrationTestContext context, Guid characterId, string questId)
    {
        return WriteQuestLogAsync(context, (questLog, scope) =>
            questLog.CompleteAsync(scope, characterId, questId, CancellationToken.None));
    }

    private static async Task WriteQuestLogAsync(
        PostgresIntegrationTestContext context,
        Func<ICharacterQuestLog, PostgresTransactionScope, Task<long>> write)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await write(
            context.Services.GetRequiredService<ICharacterQuestLog>(),
            new PostgresTransactionScope(connection, transaction));
        await transaction.CommitAsync();
    }

    // Removes the items as the system would after a lost Recovery delivery.
    private static async Task RemoveLineageItemsAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        string questId)
    {
        ItemScenarioWorld.AssertCommitted(await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<AbandonQuestItemsCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new AbandonQuestItemsCommand(characterId, null, questId)),
            CancellationToken.None));
    }

    // Fills every free permanent inventory slot with a ring, until the next grant is refused.
    private static async Task FillPermanentInventoryAsync(
        PostgresIntegrationTestContext context,
        ItemScenarioWorld world,
        Guid characterId)
    {
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        for (var slot = 0; ; slot++)
        {
            var result = await context.ItemTransactionService.ExecuteAsync(
                new ItemTransactionRequest<GrantItemCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new GrantItemCommand(characterId, null, "fixture.ring", 1, inventoryId, slot)),
                CancellationToken.None);
            if (!result.Succeeded)
            {
                Assert.True(slot > 0, result.Error?.Message);
                return;
            }
        }
    }

    private static async Task<IReadOnlyList<LineageItemRow>> LineageItemsAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        string questId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return (await connection.QueryAsync<LineageItemRow>(
                """
                select item.id as "ItemInstanceId",
                       item.definition_id as "DefinitionId",
                       item.quantity as "Quantity",
                       item.container_id as "ContainerId"
                from item_instances item
                join item_instance_policies policy
                  on policy.item_instance_id = item.id
                 and policy.policy_kind = @PolicyKind
                 and policy.status = 'active'
                 and policy.source_kind = 'quest_grant'
                 and policy.source_id = @QuestId
                join item_containers container on container.id = item.container_id
                where container.owner_character_id = @CharacterId
                order by item.id;
                """,
                new { CharacterId = characterId, QuestId = questId, PolicyKind = ItemPolicyIds.ProtectedOnDeath }))
            .ToArray();
    }

    private static async Task<string?> DestructionReasonAsync(PostgresIntegrationTestContext context, Guid itemInstanceId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "select reason from item_destructions where item_instance_id = @ItemInstanceId;",
            new { ItemInstanceId = itemInstanceId });
    }

    private sealed record LineageItemRow(Guid ItemInstanceId, string DefinitionId, int Quantity, Guid ContainerId);
}
