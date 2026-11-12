using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using static Mmorpg.GameService.Quests.Tests.QuestScenario;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Mmorpg.GameService.Quests.Tests;

// The JSON that the worker reads from a quest operation and a durable Mob corpse, written as the
// HTTP routes write it and pinned byte for byte, with the quest parts and what each response
// writes without them. GatheringQuestCreditIntegrationTests in Mmorpg.GameService.Economy.Tests
// pins a gathering yield's response, whose item response without a quest log change has no
// questLog key, which the worker reads as null. Ids and times change with every run, so they are
// replaced by placeholders in the order they first appear.
public sealed partial class WorkerResponseJsonTests
{
    private const string Trophies = "quest.fixture.trophies";
    private const string Wolf = "mob.feral_wolf";

    // The exact JSON of each response; a change here changes what the worker reads.
    private const string QuestOperationJson =
        """
        {"operationId":"<id1>","operationKind":"accept_quest","succeeded":true,"error":null,"characterRevisions":[{"characterId":"<id2>","revision":1,"carriedWeight":1,"carryCapacity":200,"currency":0}],"containerRevisions":[{"containerId":"<id3>","revision":1}],"itemRevisions":[{"itemInstanceId":"<id4>","revision":0}],"recoveryDeliveryIds":[],"characterState":{"characterId":"<id2>","itemStateRevision":1,"carriedWeight":1,"carryCapacity":200,"equipment":[]},"itemChanges":[{"itemInstanceId":"<id4>","removed":false,"definitionId":"fixture.letter","quantity":1,"revision":0,"containerId":"<id3>","containerSlotIndex":0,"equippedCharacterId":null,"equipmentSlotId":null,"policies":[{"policyKind":"protected_on_death","status":"active","protectionSource":"quest_grant"}],"bagContentsContainerId":null,"bagContentsRevision":null}],"questLog":{"characterId":"<id2>","revision":3,"quests":[{"questId":"quest.fixture.errand","stepId":"work","objectives":[{"objectiveId":"hand_over","progress":0},{"objectiveId":"ore","progress":0},{"objectiveId":"report","progress":0}],"completionCount":0}]}}
        """;

    private const string KeptQuestDropsCorpseJson =
        """
        {"operationId":"<id1>","corpse":{"corpseId":"<id2>","sourceCharacterId":null,"sourceCharacterName":null,"shardId":"local-shard-1","positionX":1.5,"positionY":0,"positionZ":-2,"rotationX":0,"rotationY":0,"rotationZ":0,"rotationW":1,"presentationKey":"corpse.mob","revision":1,"createdAt":"<time>","expiresAt":"<time>","isEmpty":false,"sections":[{"sectionKind":"bag","containerId":"<id3>","containerRevision":0,"itemCount":0},{"sectionKind":"equipment","containerId":"<id4>","containerRevision":0,"itemCount":0},{"sectionKind":"general_inventory","containerId":"<id5>","containerRevision":1,"itemCount":3}],"sourceActorDefinitionId":"mob.feral_wolf","look":null,"ownerCharacterId":"<id6>","lootGroupId":"<id7>"},"questDrops":[{"questId":"quest.fixture.trophies","definitionId":"fixture.trophy","quantity":1},{"questId":"quest.fixture.trophies","definitionId":"fixture.trophy","quantity":1}]}
        """;

    private const string NoQuestDropsCorpseJson =
        """
        {"operationId":"<id1>","corpse":{"corpseId":"<id2>","sourceCharacterId":null,"sourceCharacterName":null,"shardId":"local-shard-1","positionX":1.5,"positionY":0,"positionZ":-2,"rotationX":0,"rotationY":0,"rotationZ":0,"rotationW":1,"presentationKey":"corpse.mob","revision":1,"createdAt":"<time>","expiresAt":"<time>","isEmpty":false,"sections":[{"sectionKind":"bag","containerId":"<id3>","containerRevision":0,"itemCount":0},{"sectionKind":"equipment","containerId":"<id4>","containerRevision":0,"itemCount":0},{"sectionKind":"general_inventory","containerId":"<id5>","containerRevision":1,"itemCount":1}],"sourceActorDefinitionId":"mob.feral_wolf","look":null,"ownerCharacterId":"<id6>","lootGroupId":"<id7>"},"questDrops":[]}
        """;

    [PostgresIntegrationFact]
    public async Task AQuestOperationWritesItsQuestLogChange()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("worker_json_quest");
        await CompleteAsync(context, player.Character.Id, Wolves, "hunt");
        var session = await world.JoinAsync(player);

        var accepted = await RouteAsync(context, session, "accept_quest", Errand, Mira);

        Assert.True(accepted.Succeeded, accepted.Error?.Message);
        Assert.Equal(QuestOperationJson, WorkerJson(context, accepted.Value!));
    }

    [PostgresIntegrationFact]
    public async Task ADurableMobCorpseWritesTheQuestDropsItKept()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("worker_json_corpse");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Trophies), Mira));

        var kept = await context.CorpseService.CreatePersistentMobCorpseAsync(
            ItemScenarioWorld.WorkerId,
            CorpseRequest(
                characterId,
                [
                    new MobCorpseLootEntryRequest(Guid.NewGuid(), Ore, 2),
                    new MobCorpseLootEntryRequest(Guid.NewGuid(), Trophy, 1, Trophies),
                    new MobCorpseLootEntryRequest(Guid.NewGuid(), Trophy, 1, Trophies),
                    new MobCorpseLootEntryRequest(Guid.NewGuid(), Trophy, 1, Trophies)
                ]),
            CancellationToken.None);
        var none = await context.CorpseService.CreatePersistentMobCorpseAsync(
            ItemScenarioWorld.WorkerId,
            CorpseRequest(characterId, [new MobCorpseLootEntryRequest(Guid.NewGuid(), Ore, 1)]),
            CancellationToken.None);

        Assert.True(kept.Succeeded, kept.Error?.Message);
        Assert.True(none.Succeeded, none.Error?.Message);
        Assert.Equal(KeptQuestDropsCorpseJson, WorkerJson(context, kept.Value!));
        Assert.Equal(NoQuestDropsCorpseJson, WorkerJson(context, none.Value!));
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

    // Writes the value as the routes do, with the host's HTTP JSON options and the declared type.
    private static string WorkerJson<T>(PostgresIntegrationTestContext context, T value)
    {
        var options = context.Services.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        var json = JsonSerializer.Serialize(value, options);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        json = GuidPattern().Replace(json, match =>
        {
            if (!ids.TryGetValue(match.Value, out var index))
            {
                index = ids.Count + 1;
                ids.Add(match.Value, index);
            }

            return "<id" + index + ">";
        });
        return TimePattern().Replace(json, "<time>");
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?")]
    private static partial Regex TimePattern();
}
