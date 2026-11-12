using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Quests;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Mmorpg.GameService.Economy.Tests;

// A gathering yield counts for the gather objectives of its node through Quests' IQuestGatherCredit
// in the yield's transaction, with the fixture errand quest.fixture.errand at its first step, and
// the worker item response passes the quest log change through, pinned byte for byte as the route
// writes it. Ids are replaced by placeholders in the order they first appear.
public sealed partial class GatheringQuestCreditIntegrationTests
{
    private const string Mira = "npc.city_services";
    private const string Wolves = "quest.fixture.wolves";
    private const string Errand = "quest.fixture.errand";
    private const string Ore = "fixture.ore";

    // The exact JSON of each response; a change here changes what the worker reads.
    private const string CountedGatherJson =
        """
        {"operationId":"<id1>","operationKind":"gather_resource","succeeded":true,"error":null,"characterRevisions":[{"characterId":"<id2>","revision":4,"carriedWeight":7,"carryCapacity":200,"currency":0}],"containerRevisions":[{"containerId":"<id3>","revision":4}],"itemRevisions":[{"itemInstanceId":"<id4>","revision":0}],"recoveryDeliveryIds":[],"characterState":{"characterId":"<id2>","itemStateRevision":4,"carriedWeight":7,"carryCapacity":200,"equipment":[{"equipmentSlotId":"tool","itemInstanceId":"<id5>","definitionId":"fixture.pickaxe","quantity":1,"revision":1}]},"itemChanges":[{"itemInstanceId":"<id4>","removed":false,"definitionId":"fixture.ore","quantity":1,"revision":0,"containerId":"<id3>","containerSlotIndex":1,"equippedCharacterId":null,"equipmentSlotId":null,"policies":[],"bagContentsContainerId":null,"bagContentsRevision":null}],"questLog":{"characterId":"<id2>","revision":4,"quests":[{"questId":"quest.fixture.errand","stepId":"work","objectives":[{"objectiveId":"hand_over","progress":0},{"objectiveId":"ore","progress":1},{"objectiveId":"report","progress":0}],"completionCount":0}]}}
        """;

    private const string UncountedGatherJson =
        """
        {"operationId":"<id1>","operationKind":"gather_resource","succeeded":true,"error":null,"characterRevisions":[{"characterId":"<id2>","revision":5,"carriedWeight":13,"carryCapacity":200,"currency":0}],"containerRevisions":[{"containerId":"<id3>","revision":5}],"itemRevisions":[{"itemInstanceId":"<id4>","revision":1}],"recoveryDeliveryIds":[],"characterState":{"characterId":"<id2>","itemStateRevision":5,"carriedWeight":13,"carryCapacity":200,"equipment":[{"equipmentSlotId":"tool","itemInstanceId":"<id5>","definitionId":"fixture.pickaxe","quantity":1,"revision":1}]},"itemChanges":[{"itemInstanceId":"<id4>","removed":false,"definitionId":"fixture.ore","quantity":2,"revision":1,"containerId":"<id3>","containerSlotIndex":1,"equippedCharacterId":null,"equipmentSlotId":null,"policies":[],"bagContentsContainerId":null,"bagContentsRevision":null}]}
        """;

    [PostgresIntegrationFact]
    public async Task GatheringYieldsCountForTheGatherObjectivesOfTheirNode()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (world, player, session) = await JoinErrandAsync(context, "quest_miner");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var pickaxe = await world.GrantAsync(characterId, "fixture.pickaxe", 1, state.PermanentInventoryContainerId, 5);
        await world.EquipAsync(characterId, pickaxe, "tool");

        var vein = await GatherAsync(context, session, characterId, "resource.iron_vein");
        var tree = await GatherAsync(context, session, characterId, "resource.oak_tree");
        var unnamed = await GatherAsync(context, session, characterId, null);
        var again = await GatherAsync(context, session, characterId, "resource.iron_vein");

        Assert.Equal(1, Progress(vein, Errand, "ore"));
        Assert.Null(QuestLogSection.Read(tree));
        Assert.Null(QuestLogSection.Read(unnamed));
        Assert.Equal(2, Progress(again, Errand, "ore"));
        // Every yield lands in carried storage, whatever it counted for.
        Assert.Equal([(1, 4)], await InventoryStacksAsync(context, world, characterId, Ore));
    }

    [PostgresIntegrationFact]
    public async Task AGatheringYieldWritesItsQuestLogChangeOrNoQuestLog()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (world, player, session) = await JoinErrandAsync(context, "worker_json_gatherer");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var pickaxe = await world.GrantAsync(characterId, "fixture.pickaxe", 1, state.PermanentInventoryContainerId, 5);
        await world.EquipAsync(characterId, pickaxe, "tool");

        var counted = await RouteGatherAsync(context, session, "resource.iron_vein");
        var uncounted = await RouteGatherAsync(context, session, "resource.oak_tree");

        Assert.Equal(CountedGatherJson, WorkerJson(context, counted));
        Assert.Equal(UncountedGatherJson, WorkerJson(context, uncounted));
    }

    // A character at the errand's first step, with its letter: the wolves quest it requires is
    // completed in the quest log, and the errand is accepted at Mira as the worker would after
    // validating the interaction.
    private static async Task<(ItemScenarioWorld World, IntegrationPlayer Player, ConsumedSimulationJoinTicketResponse Session)> JoinErrandAsync(
        PostgresIntegrationTestContext context,
        string name)
    {
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync(name);
        var characterId = player.Character.Id;
        await WriteQuestLogAsync(context, (questLog, scope) =>
            questLog.AcceptAsync(scope, characterId, Wolves, "hunt", [], CancellationToken.None));
        await WriteQuestLogAsync(context, (questLog, scope) =>
            questLog.CompleteAsync(scope, characterId, Wolves, CancellationToken.None));
        var session = await world.JoinAsync(player);
        ItemScenarioWorld.AssertCommitted(await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<AcceptQuestCommand>(
                Guid.NewGuid(),
                ItemScenarioWorld.SimulationActor(
                    session,
                    QuestNpcAccess.Claim(ItemTransactionLiveAccess.None, Mira)),
                new AcceptQuestCommand(characterId, Errand)),
            CancellationToken.None));
        return (world, player, session);
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

    private static async Task<ItemTransactionResult> GatherAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        Guid characterId,
        string? resourceActorId)
    {
        var result = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GatherResourceCommand>(
                Guid.NewGuid(),
                ItemScenarioWorld.SimulationActor(session),
                new GatherResourceCommand(characterId, "resource.fixture", "fixture.node:0000", "pickaxe", Ore, 1, resourceActorId)),
            CancellationToken.None);
        ItemScenarioWorld.AssertCommitted(result);
        return result;
    }

    private static async Task<SimulationItemOperationResponse> RouteGatherAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        string resourceActorId)
    {
        await using var scope = context.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SimulationItemMutationService>().ExecuteAsync(
            session.WorkerId,
            session.SimulationSessionId,
            new SimulationItemOperationRequest(
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                new SimulationItemAccessRequest(false, false, false),
                EconomyOperationKinds.GatherResource,
                0,
                Quantity: 1,
                InteractionSessionId: Guid.NewGuid(),
                CapabilityId: "resource.mine")
                .WithEconomyFields(new EconomyOperationFields(
                    DefinitionId: Ore,
                    ResourceProfileId: "resource.fixture",
                    ResourceNodeId: "fixture.node:0000",
                    RequiredToolKind: "pickaxe",
                    ResourceActorId: resourceActorId)),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
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

    // Every carried quantity of a definition in the permanent inventory, by slot.
    private static async Task<IReadOnlyList<(int Slot, int Quantity)>> InventoryStacksAsync(
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

    // Writes the value as the routes do, with the host's HTTP JSON options and the declared type.
    private static string WorkerJson<T>(PostgresIntegrationTestContext context, T value)
    {
        var options = context.Services.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        var json = JsonSerializer.Serialize(value, options);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        return GuidPattern().Replace(json, match =>
        {
            if (!ids.TryGetValue(match.Value, out var index))
            {
                index = ids.Count + 1;
                ids.Add(match.Value, index);
            }

            return "<id" + index + ">";
        });
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex GuidPattern();
}
