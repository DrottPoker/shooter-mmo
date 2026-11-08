using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items.Tests.ItemTransactions;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Tests;

// Quest scenarios against the fixture quest catalog: quest.fixture.wolves at Mira,
// quest.fixture.errand, which requires it and grants a fixture.letter, and the repeatable
// quest.fixture.ore_run.
internal static class QuestScenario
{
    public const string Mira = "npc.city_services";
    public const string Guard = "npc.city_guard";
    public const string Wolves = "quest.fixture.wolves";
    public const string Errand = "quest.fixture.errand";
    public const string OreRun = "quest.fixture.ore_run";
    public const string Letter = "fixture.letter";
    public const string Trophy = "fixture.trophy";
    public const string Ore = "fixture.ore";

    // Runs a quest command as the worker would after validating an interaction with the NPC.
    public static Task<ItemTransactionResult> ExecuteAsync<TCommand>(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        TCommand command,
        string? questNpcActorId,
        Guid? operationId = null)
        where TCommand : notnull
    {
        var service = context.ItemTransactionService;
        var request = new ItemTransactionRequest<TCommand>(
            operationId ?? Guid.NewGuid(),
            ItemScenarioWorld.SimulationActor(
                session,
                new ItemTransactionLiveAccess(false, false, false, QuestNpcActorId: questNpcActorId)),
            command);
        var none = CancellationToken.None;
        return request switch
        {
            ItemTransactionRequest<AcceptQuestCommand> accept => service.ExecuteAsync(accept, none),
            ItemTransactionRequest<AbandonQuestCommand> abandon => service.ExecuteAsync(abandon, none),
            ItemTransactionRequest<RegrantQuestItemsCommand> regrant => service.ExecuteAsync(regrant, none),
            ItemTransactionRequest<TalkToQuestNpcCommand> talk => service.ExecuteAsync(talk, none),
            ItemTransactionRequest<DeliverQuestItemsCommand> deliver => service.ExecuteAsync(deliver, none),
            ItemTransactionRequest<CreditQuestKillCommand> kill => service.ExecuteAsync(kill, none),
            ItemTransactionRequest<TurnInQuestCommand> turnIn => service.ExecuteAsync(turnIn, none),
            ItemTransactionRequest<GatherResourceCommand> gather => service.ExecuteAsync(gather, none),
            _ => throw new ArgumentException("Not a quest scenario command.", nameof(command))
        };
    }

    public static async Task<ServiceResult<SimulationItemOperationResponse>> RouteAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        string operationKind,
        string? questId,
        string? questNpcActorId,
        string? objectiveId = null,
        string? rewardChoiceDefinitionId = null,
        string? mobActorId = null,
        IReadOnlyList<string>? mobTags = null,
        string? questCatalogRevision = null)
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
                questNpcActorId,
                objectiveId,
                rewardChoiceDefinitionId,
                mobActorId,
                mobTags,
                questCatalogRevision ?? PostgresIntegrationTestContext.FixtureQuestCatalogRevision),
            CancellationToken.None);
    }

    public static async Task<CharacterQuestLogState> ReadQuestLogAsync(
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
    public static async Task CompleteAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        string questId,
        string stepId)
    {
        await WriteQuestLogAsync(context, (questLog, scope) =>
            questLog.AcceptAsync(scope, characterId, questId, stepId, [], CancellationToken.None));
        await CompleteActiveAsync(context, characterId, questId);
    }

    public static Task CompleteActiveAsync(PostgresIntegrationTestContext context, Guid characterId, string questId)
    {
        return WriteQuestLogAsync(context, (questLog, scope) =>
            questLog.CompleteAsync(scope, characterId, questId, CancellationToken.None));
    }

    public static Task SetProgressAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        string questId,
        string objectiveId,
        int progress)
    {
        return WriteQuestLogAsync(context, (questLog, scope) => questLog.SetProgressAsync(
            scope,
            characterId,
            questId,
            new Dictionary<string, int> { [objectiveId] = progress },
            CancellationToken.None));
    }

    public static Task AdvanceAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        string questId,
        string stepId,
        string[] objectiveIds)
    {
        return WriteQuestLogAsync(context, (questLog, scope) =>
            questLog.AdvanceAsync(scope, characterId, questId, stepId, objectiveIds, CancellationToken.None));
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
    public static async Task RemoveLineageItemsAsync(
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
    public static async Task FillPermanentInventoryAsync(
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

    public static async Task<IReadOnlyList<LineageItemRow>> LineageItemsAsync(
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

    public static async Task<string?> DestructionReasonAsync(PostgresIntegrationTestContext context, Guid itemInstanceId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "select reason from item_destructions where item_instance_id = @ItemInstanceId;",
            new { ItemInstanceId = itemInstanceId });
    }

    // Every carried quantity of a definition, by container and slot.
    public static async Task<IReadOnlyList<(int Slot, int Quantity)>> InventoryStacksAsync(
        PostgresIntegrationTestContext context,
        ItemScenarioWorld world,
        Guid characterId,
        string definitionId)
    {
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return (await connection.QueryAsync<(int, int)>(
                """
                select container_slot_index, quantity
                from item_instances
                where container_id = @InventoryId and definition_id = @DefinitionId
                order by container_slot_index;
                """,
                new { InventoryId = inventoryId, DefinitionId = definitionId }))
            .ToArray();
    }

    internal sealed record LineageItemRow(Guid ItemInstanceId, string DefinitionId, int Quantity, Guid ContainerId);
}
