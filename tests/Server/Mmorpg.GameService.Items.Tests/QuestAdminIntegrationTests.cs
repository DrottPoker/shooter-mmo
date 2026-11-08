using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items.Tests.ItemTransactions;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using static Mmorpg.GameService.Items.Tests.QuestScenario;

namespace Mmorpg.GameService.Items.Tests;

// The Admin menu's quest tools on the worker quest route, with the fixture quests of
// QuestScenario. They go around the givers, the level, and the required quests, and leave the
// quest log where the normal quest actions go on.
public sealed class QuestAdminIntegrationTests
{
    private const string Trophies = "quest.fixture.trophies";

    // The errand requires the wolves, which the character never did.
    [PostgresIntegrationFact]
    public async Task AnAdminAcceptGoesAroundTheGiverAndTheRequiredQuests()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_admin_accepter");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);

        var accepted = await RouteAsync(context, session, "admin_accept_quest", Errand, null);
        var twice = await RouteAsync(context, session, "admin_accept_quest", Errand, null);

        Assert.True(accepted.Succeeded, accepted.Error?.Message);
        Assert.Equal((Errand, "work"), OnlyQuest(accepted));
        Assert.Equal(Letter, Assert.Single(await LineageItemsAsync(context, characterId, Errand)).DefinitionId);
        Assert.Equal((409, ItemTransactionErrorCodes.QuestAlreadyActive), (twice.StatusCode, twice.Error!.Code));
    }

    // An objective, then the rest of the work step, then the return step; the letter from the
    // accept is still carried, so making the errand ready changes nothing, and Mira takes it as
    // usual.
    [PostgresIntegrationFact]
    public async Task CompletedObjectivesAndStepsLeaveAQuestThatTheNormalTurnInFinishes()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_admin_completer");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        Assert.True((await RouteAsync(context, session, "admin_accept_quest", Errand, null)).Succeeded);

        var report = await RouteAsync(context, session, "admin_complete_quest_objective", Errand, null, objectiveId: "report");
        var ghost = await RouteAsync(context, session, "admin_complete_quest_objective", Errand, null, objectiveId: "ghost");
        var work = await RouteAsync(context, session, "admin_complete_quest_step", Errand, null);
        var ready = await RouteAsync(context, session, "admin_make_quest_ready", Errand, null);
        var turnedIn = await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, Errand, null), Mira);

        Assert.True(report.Succeeded, report.Error?.Message);
        var reported = Assert.Single(report.Value!.QuestLog!.Quests);
        Assert.Equal("work", reported.StepId);
        Assert.Contains(new ItemTransactionQuestObjectiveProgress("report", 1), reported.Objectives);
        Assert.Contains(new ItemTransactionQuestObjectiveProgress("ore", 0), reported.Objectives);
        Assert.Equal(ItemTransactionErrorCodes.QuestObjectiveInvalid, ghost.Error!.Code);
        Assert.True(work.Succeeded, work.Error?.Message);
        Assert.Equal((Errand, "return"), OnlyQuest(work));
        Assert.True(ready.Succeeded, ready.Error?.Message);
        Assert.Null(ready.Value!.QuestLog);
        ItemScenarioWorld.AssertCommitted(turnedIn);
        Assert.Equal(1, Assert.Single(turnedIn.QuestLog!.Quests).CompletionCount);
    }

    // The trophies collect two quest letters, the ore run three ordinary ores, and the wolves
    // count two kills; making each ready lets Mira take it.
    [PostgresIntegrationFact]
    public async Task MakingAQuestReadyGrantsWhatItsCollectObjectivesLack()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_admin_readier");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        foreach (var questId in new[] { Trophies, OreRun, Wolves })
        {
            Assert.True((await RouteAsync(context, session, "admin_accept_quest", questId, null)).Succeeded, questId);
            var ready = await RouteAsync(context, session, "admin_make_quest_ready", questId, null);
            Assert.True(ready.Succeeded, ready.Error?.Message);
        }

        Assert.Equal(2, (await LineageItemsAsync(context, characterId, Trophies)).Sum(item => item.Quantity));
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, Trophies, null), Mira));
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, OreRun, null), Mira));
        ItemScenarioWorld.AssertCommitted(
            await ExecuteAsync(context, session, new TurnInQuestCommand(characterId, Wolves, "fixture.sword"), Mira));
        var log = await ReadQuestLogAsync(context, characterId);
        Assert.Empty(log.ActiveQuests);
        Assert.Equal(3, log.CompletedQuests.Count);
    }

    // A reset forgets a quest with its lineage items, the chain also the quests that require
    // it, and the full reset everything, after which Mira offers the wolves as at first. A
    // quest the log does not hold changes nothing.
    [PostgresIntegrationFact]
    public async Task ResettingForgetsQuestsWithTheirItemsAndCanTakeTheFollowUps()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_admin_resetter");
        var characterId = player.Character.Id;
        var session = await world.JoinAsync(player);
        await CompleteAsync(context, characterId, Wolves, "hunt");
        Assert.True((await RouteAsync(context, session, "admin_accept_quest", Errand, null)).Succeeded);
        Assert.True((await RouteAsync(context, session, "admin_accept_quest", OreRun, null)).Succeeded);
        var letterId = Assert.Single(await LineageItemsAsync(context, characterId, Errand)).ItemInstanceId;

        var nothing = await RouteAsync(context, session, "admin_reset_quest", Trophies, null);
        var chain = await RouteAsync(context, session, "admin_reset_quest_chain", Wolves, null);
        var afterChain = await ReadQuestLogAsync(context, characterId);
        var everything = await RouteAsync(context, session, "admin_reset_quests", null, null);
        var again = await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira);

        Assert.True(nothing.Succeeded, nothing.Error?.Message);
        Assert.Null(nothing.Value!.QuestLog);
        Assert.True(chain.Succeeded, chain.Error?.Message);
        Assert.Equal(
            [(Errand, (string?)null, 0), (Wolves, null, 0)],
            chain.Value!.QuestLog!.Quests.Select(quest => (quest.QuestId, quest.StepId, quest.CompletionCount)));
        Assert.Empty(await LineageItemsAsync(context, characterId, Errand));
        Assert.Equal(["quest_reset"], await DestructionReasonsAsync(context, letterId));
        Assert.Equal(OreRun, Assert.Single(afterChain.ActiveQuests).QuestId);
        Assert.Empty(afterChain.CompletedQuests);
        Assert.True(everything.Succeeded, everything.Error?.Message);
        Assert.Equal((OreRun, null), OnlyQuest(everything));
        ItemScenarioWorld.AssertCommitted(again);
    }

    [PostgresIntegrationFact]
    public async Task OnlyADevelopmentGameServiceRunsTheTools()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("quest_admin_outsider");
        var session = await world.JoinAsync(player);
        await using var scope = context.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var release = new SimulationQuestMutationService(
            provider.GetRequiredService<ItemTransactionService>(),
            provider.GetRequiredService<SimulationCharacterStates>(),
            provider.GetRequiredService<ItemsModuleOptions>() with { DevelopmentQuestTools = false },
            provider.GetRequiredService<IQuestDefinitions>());

        var refused = await release.ExecuteAsync(
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
                "admin_accept_quest",
                Wolves,
                QuestCatalogRevision: PostgresIntegrationTestContext.FixtureQuestCatalogRevision),
            CancellationToken.None);
        var resetAllWithAQuest = await RouteAsync(context, session, "admin_reset_quests", Wolves, null);

        Assert.Equal((403, ItemTransactionErrorCodes.DevelopmentToolsDisabled), (refused.StatusCode, refused.Error!.Code));
        Assert.Empty((await ReadQuestLogAsync(context, player.Character.Id)).ActiveQuests);
        Assert.Equal(400, resetAllWithAQuest.StatusCode);
    }

    private static (string QuestId, string? StepId) OnlyQuest(ServiceResult<SimulationItemOperationResponse> result)
    {
        var quest = Assert.Single(result.Value!.QuestLog!.Quests);
        return (quest.QuestId, quest.StepId);
    }

    private static async Task<string[]> DestructionReasonsAsync(PostgresIntegrationTestContext context, Guid itemInstanceId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return (await connection.QueryAsync<string>(
                "select reason from item_destructions where item_instance_id = @ItemInstanceId;",
                new { ItemInstanceId = itemInstanceId }))
            .ToArray();
    }
}
