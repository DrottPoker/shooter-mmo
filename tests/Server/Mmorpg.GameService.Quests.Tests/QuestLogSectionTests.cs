using System.Text.Json;
using Dapper;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using static Mmorpg.GameService.Quests.Tests.QuestScenario;

namespace Mmorpg.GameService.Quests.Tests;

// The quest log change of a quest command is the questLog section of its item transaction
// result, which the kernel stores and replays without reading it and the worker response passes
// through. Results stored before the sections had the same key with the same shape, and every
// other result held "questLog": null, so they replay the same quest log change.
public sealed class QuestLogSectionTests
{
    [PostgresIntegrationFact]
    public async Task AQuestLogStoredInTheEarlierShapeReplaysAsTheSameChange()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (world, characterId, session) = await JoinWithWolvesDoneAsync(context, "section_old_quest_log");
        var operationId = Guid.NewGuid();
        ItemScenarioWorld.AssertCommitted(await AcceptErrandAsync(context, session, characterId, operationId));
        await StoreAsync(
            world,
            operationId,
            $$"""
            {"error": null, "questLog": {"quests": [{"stepId": "work", "questId": "quest.fixture.errand", "objectives": [{"progress": 0, "objectiveId": "hand_over"}, {"progress": 2, "objectiveId": "ore"}, {"progress": 1, "objectiveId": "report"}], "completionCount": 0}], "revision": 7, "characterId": "{{characterId}}"}, "succeeded": true, "itemChanges": [], "operationId": "{{operationId}}", "itemRevisions": [], "operationKind": "accept_quest", "containerRevisions": [], "characterRevisions": [], "recoveryDeliveryIds": []}
            """);

        var replayed = await AcceptErrandAsync(context, session, characterId, operationId);

        var expected = new ItemTransactionQuestLogChange(
            characterId,
            7,
            [
                new ItemTransactionQuestChange(
                    Errand,
                    "work",
                    [
                        new ItemTransactionQuestObjectiveProgress("hand_over", 0),
                        new ItemTransactionQuestObjectiveProgress("ore", 2),
                        new ItemTransactionQuestObjectiveProgress("report", 1)
                    ],
                    0)
            ]);
        ItemScenarioWorld.AssertCommitted(replayed);
        Assert.Equal(Json(expected), Json(QuestLogSection.Read(replayed)));
        Assert.Equal(Json(expected), Json(QuestLogOf(SimulationItemOperationResponse.From(replayed, null))));
        Assert.Null(QuestDropSection.Read(replayed));
    }

    // The worker response leaves the key out, as it does for every result without a quest log
    // change; the worker reads a missing questLog as null.
    [PostgresIntegrationFact]
    public async Task ANullQuestLogStoredInTheEarlierShapeReplaysAsNoQuestLog()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (world, characterId, session) = await JoinWithWolvesDoneAsync(context, "section_old_null");
        var operationId = Guid.NewGuid();
        ItemScenarioWorld.AssertCommitted(await AcceptErrandAsync(context, session, characterId, operationId));
        await StoreAsync(
            world,
            operationId,
            $$"""
            {"error": null, "questLog": null, "succeeded": true, "itemChanges": null, "operationId": "{{operationId}}", "itemRevisions": [], "operationKind": "accept_quest", "containerRevisions": [], "characterRevisions": [], "recoveryDeliveryIds": []}
            """);

        var replayed = await AcceptErrandAsync(context, session, characterId, operationId);
        var response = SimulationItemOperationResponse.From(replayed, null);

        ItemScenarioWorld.AssertCommitted(replayed);
        Assert.Null(replayed.Section(QuestLogSection.Name));
        Assert.Null(QuestLogSection.Read(replayed));
        Assert.Null(response.Sections);
        Assert.EndsWith(
            "\"itemChanges\":null}",
            JsonSerializer.Serialize(response, JsonSerializerOptions.Web),
            StringComparison.Ordinal);
    }

    // A result written now keeps a section as a top-level key of the stored JSON with the shape
    // of its value, and a result without a quest part stores no quest key.
    [PostgresIntegrationFact]
    public async Task AResultWrittenNowStoresItsSectionAsATopLevelKeyAndReplaysIt()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (world, characterId, session) = await JoinWithWolvesDoneAsync(context, "section_round_trip");
        var operationId = Guid.NewGuid();

        var first = await AcceptErrandAsync(context, session, characterId, operationId);
        var replayed = await AcceptErrandAsync(context, session, characterId, operationId);
        using var stored = JsonDocument.Parse(await LoadPayloadAsync(world, operationId));
        var state = await world.LoadStateAsync(characterId);
        var grantId = Guid.NewGuid();
        ItemScenarioWorld.AssertCommitted(await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                grantId,
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(characterId, null, Ore, 1, state.PermanentInventoryContainerId, 5)),
            CancellationToken.None));
        using var plain = JsonDocument.Parse(await LoadPayloadAsync(world, grantId));

        ItemScenarioWorld.AssertCommitted(first);
        var change = QuestLogSection.Read(first);
        Assert.NotNull(change);
        Assert.Equal(Errand, Assert.Single(change.Quests).QuestId);
        Assert.Equal(
            Json(change),
            Json(stored.RootElement.GetProperty(QuestLogSection.Name)
                .Deserialize<ItemTransactionQuestLogChange>(ItemOperationJson.Options)));
        Assert.False(stored.RootElement.TryGetProperty(QuestDropSection.Name, out _));
        Assert.Equal(Json(change), Json(QuestLogSection.Read(replayed)));
        Assert.Equal(Json(change), Json(QuestLogOf(SimulationItemOperationResponse.From(replayed, null))));
        Assert.False(plain.RootElement.TryGetProperty(QuestLogSection.Name, out _));
        Assert.False(plain.RootElement.TryGetProperty(QuestDropSection.Name, out _));
    }

    private static async Task<(ItemScenarioWorld World, Guid CharacterId, ConsumedSimulationJoinTicketResponse Session)> JoinWithWolvesDoneAsync(
        PostgresIntegrationTestContext context,
        string name)
    {
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync(name);
        await CompleteAsync(context, player.Character.Id, Wolves, "hunt");
        return (world, player.Character.Id, await world.JoinAsync(player));
    }

    private static Task<ItemTransactionResult> AcceptErrandAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        Guid characterId,
        Guid operationId)
    {
        return ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Errand), Mira, operationId);
    }

    private static Task StoreAsync(ItemScenarioWorld world, Guid operationId, string payload)
    {
        return world.ExecuteSqlAsync(
            """
            update item_operations
            set result_payload = cast(@Payload as jsonb)
            where operation_id = @OperationId
              and status = 'committed';
            """,
            new { OperationId = operationId, Payload = payload });
    }

    private static async Task<string> LoadPayloadAsync(ItemScenarioWorld world, Guid operationId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<string>(
            "select result_payload::text from item_operations where operation_id = @OperationId;",
            new { OperationId = operationId });
    }

    private static string Json<T>(T value)
    {
        return JsonSerializer.Serialize(value, ItemOperationJson.Options);
    }
}
