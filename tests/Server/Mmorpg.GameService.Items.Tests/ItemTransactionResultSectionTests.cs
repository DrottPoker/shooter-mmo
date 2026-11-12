using System.Text.Json;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// The named sections of an item transaction result are top-level keys of the stored JSON that the
// kernel stores and replays without reading them, such as a durable Mob corpse's kept quest drops
// under questDrops. Results stored before the sections had the same keys with the same shapes. The
// worker response passes every section through. The quest log section is pinned in
// Mmorpg.GameService.Quests.Tests.
public sealed class ItemTransactionResultSectionTests
{
    private const string Trophies = "quest.fixture.trophies";
    private const string Trophy = "fixture.trophy";
    private const string Ore = "fixture.ore";
    private const string Wolf = "mob.feral_wolf";

    [PostgresIntegrationFact]
    public async Task QuestDropsStoredInTheEarlierShapeReplayToTheWorker()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("section_old_quest_drops");
        var characterId = player.Character.Id;
        await world.JoinAsync(player);
        var request = CorpseRequest(characterId, [new MobCorpseLootEntryRequest(Guid.NewGuid(), Ore, 1)]);
        var created = await context.CorpseService.CreatePersistentMobCorpseAsync(
            ItemScenarioWorld.WorkerId,
            request,
            CancellationToken.None);
        Assert.True(created.Succeeded, created.Error?.Message);
        Assert.Empty(created.Value!.QuestDrops);
        await world.ExecuteSqlAsync(
            """
            update item_operations
            set result_payload = cast(@Payload as jsonb)
            where operation_id = @OperationId
              and status = 'committed';
            """,
            new
            {
                request.OperationId,
                Payload = $$"""
                    {"error": null, "questLog": null, "succeeded": true, "questDrops": [{"quantity": 1, "questId": "{{Trophies}}", "definitionId": "{{Trophy}}"}, {"quantity": 2, "questId": "{{Trophies}}", "definitionId": "{{Trophy}}"}], "itemChanges": null, "operationId": "{{request.OperationId}}", "itemRevisions": [], "operationKind": "create_persistent_mob_corpse", "containerRevisions": [], "characterRevisions": [], "recoveryDeliveryIds": []}
                    """
            });

        var replayed = await context.CorpseService.CreatePersistentMobCorpseAsync(
            ItemScenarioWorld.WorkerId,
            request,
            CancellationToken.None);

        Assert.True(replayed.Succeeded, replayed.Error?.Message);
        Assert.Equal(
            [new ItemTransactionQuestDrop(Trophies, Trophy, 1), new ItemTransactionQuestDrop(Trophies, Trophy, 2)],
            replayed.Value!.QuestDrops);
    }

    // Every section that holds a value passes through in the result's order, after the response's
    // own properties; a null section and a section named like a response property do not.
    [Fact]
    public void TheWorkerResponsePassesEverySectionThrough()
    {
        var result = new ItemTransactionResult(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            ItemOperationKinds.Grant,
            true,
            null,
            [],
            [],
            [],
            [])
        {
            Sections = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["questLog"] = JsonSerializer.SerializeToElement(new { revision = 3 }),
                ["empty"] = JsonSerializer.SerializeToElement<object?>(null),
                ["CharacterState"] = JsonSerializer.SerializeToElement(1),
                ["questDrops"] = JsonSerializer.SerializeToElement(new[] { 1, 2 })
            }
        };

        var response = SimulationItemOperationResponse.From(result, null);

        Assert.Equal(new[] { "questLog", "questDrops" }, response.Sections!.Keys);
        Assert.EndsWith(
            "\"characterState\":null,\"itemChanges\":null,\"questLog\":{\"revision\":3},\"questDrops\":[1,2]}",
            JsonSerializer.Serialize(response, JsonSerializerOptions.Web),
            StringComparison.Ordinal);
    }

    private static CreatePersistentMobCorpseRequest CorpseRequest(
        Guid ownerCharacterId,
        IReadOnlyList<MobCorpseLootEntryRequest> loot)
    {
        return new CreatePersistentMobCorpseRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ItemScenarioWorld.WorkerId,
            ItemScenarioWorld.WorkerRuntimeId,
            ItemScenarioWorld.ShardId,
            Wolf,
            1.5d,
            0d,
            -2d,
            0d,
            0d,
            0d,
            1d,
            ItemScenarioWorld.MobCorpsePresentationKey,
            90d,
            loot)
        {
            OwnerCharacterId = ownerCharacterId,
            LootGroupId = Guid.NewGuid()
        };
    }
}
