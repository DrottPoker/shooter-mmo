using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Combat;
using Mmorpg.WorldData.Progression;
using static Mmorpg.GameService.Quests.Tests.QuestScenario;

namespace Mmorpg.GameService.Quests.Tests;

// A Mob kill credit grants the experience of the kill and counts the kill for the quests in one
// transaction, a turn-in grants the quest's experience reward, and the Admin menu sets a level or
// grants experience. Each reports the character's progression in the result's progression section.
// By the progression rules of the content, level 1 needs 100 and a level 1 Mob gives 20.
public sealed class MobKillExperienceIntegrationTests
{
    private const string Wolf = "mob.feral_wolf";

    [PostgresIntegrationFact]
    public async Task AKillCreditGrantsExperienceAndCountsTheQuestInOneTransaction()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "experience_hunter");
        var characterId = player.Character.Id;
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira));

        var kill = await KillAsync(context, session, 1, 100);

        var progression = ProgressionOf(kill);
        Assert.Equal(
            new ItemTransactionProgressionChange(characterId, 1, 20, 100, -5, 20, 0),
            progression);
        Assert.Equal(1, Assert.Single(QuestLogOf(kill.Value!)!.Quests).Objectives.Single().Progress);
        Assert.Equal((1, 20L), await StoredAsync(context, characterId));
        var audit = await AuditAsync(context, kill.Value!.OperationId, "mob_kill_credited");
        Assert.Equal(Wolf, audit.GetProperty("mobActorId").GetString());
        Assert.Equal(1, audit.GetProperty("mobLevel").GetInt32());
        Assert.Equal(20, audit.GetProperty("experienceGained").GetInt64());
        Assert.Equal(1, audit.GetProperty("levelAfter").GetInt32());
        Assert.Equal([Wolves], audit.GetProperty("quests").EnumerateArray().Select(quest => quest.GetString()));

        await EconomyAudit.AssertCleanAsync(context);
    }

    [PostgresIntegrationFact]
    public async Task AKillThatNoQuestAsksForStillGrantsItsExperience()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "experience_wanderer");

        var kill = await KillAsync(context, session, 3, 100);

        // A level 3 Mob gives 30: the rails count Mobs up to 5 levels above level 1 as themselves.
        Assert.Equal((1, 30L, 30L, 0), Summary(ProgressionOf(kill)));
        Assert.Empty(QuestLogOf(kill.Value!)!.Quests);
        Assert.Equal((1, 30L), await StoredAsync(context, player.Character.Id));

        await EconomyAudit.AssertCleanAsync(context);
    }

    [PostgresIntegrationFact]
    public async Task AReplayGrantsOnceAndReturnsTheSameProgression()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "experience_replayer");
        var operationId = Guid.NewGuid();

        var first = await KillAsync(context, session, 1, 100, operationId);
        var replayed = await KillAsync(context, session, 1, 100, operationId);

        // The replay reads the stored result, whose JSON keeps the values but not the key order.
        Assert.Equal(ProgressionOf(first), ProgressionOf(replayed));
        Assert.Equal((1, 20L, 20L, 0), Summary(ProgressionOf(replayed)));
        Assert.Equal((1, 20L), await StoredAsync(context, player.Character.Id));

        await EconomyAudit.AssertCleanAsync(context);
    }

    [PostgresIntegrationFact]
    public async Task AMobWithoutExperienceStillCountsForTheQuest()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "experience_pacifist");
        var characterId = player.Character.Id;
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira));

        var kill = await KillAsync(context, session, 1, 0);

        Assert.Equal((1, 0L, 0L, 0), Summary(ProgressionOf(kill)));
        Assert.Equal(1, Assert.Single(QuestLogOf(kill.Value!)!.Quests).Objectives.Single().Progress);
        Assert.Equal((1, 0L), await StoredAsync(context, characterId));

        await EconomyAudit.AssertCleanAsync(context);
    }

    [PostgresIntegrationFact]
    public async Task AnInvalidLevelOrPercentIsRefused()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "experience_forger");

        ServiceResult<SimulationItemOperationResponse>[] refused =
        [
            await KillAsync(context, session, 0, 100),
            await KillAsync(context, session, CombatContentRules.MaximumCharacterLevel + 1, 100),
            await KillAsync(context, session, 1, -1),
            await KillAsync(context, session, 1, ProgressionRulesFormat.MaximumMobExperiencePercent + 1),
            await RouteAsync(context, session, "credit_mob_kill", null, null, mobActorId: Wolf, mobTags: [], mobExperiencePercent: 100),
            await RouteAsync(context, session, "credit_mob_kill", null, null, mobActorId: Wolf, mobTags: [], mobLevel: 1)
        ];

        Assert.All(refused, result => Assert.Equal(
            (400, ItemApiErrorCodes.SimulationOperationInvalid),
            (result.StatusCode, result.Error!.Code)));
        Assert.Equal((1, 0L), await StoredAsync(context, player.Character.Id));

        await EconomyAudit.AssertCleanAsync(context);
    }

    // Kills without experience make the hunt ready, so the turn-in alone takes level 1 to 2.
    [PostgresIntegrationFact]
    public async Task ATurnInGrantsTheQuestsExperienceReward()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "experience_veteran");
        var characterId = player.Character.Id;
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(characterId, Wolves), Mira));
        await KillAsync(context, session, 1, 0);
        await KillAsync(context, session, 1, 0);

        var turnedIn = await RouteAsync(context, session, "turn_in_quest", Wolves, Mira, rewardChoiceDefinitionId: "fixture.bow");

        Assert.True(turnedIn.Succeeded, turnedIn.Error?.Message);
        Assert.Equal((2, 0L, 100L, 1), Summary(ProgressionOf(turnedIn)));
        Assert.Equal(150, ProgressionOf(turnedIn).ExperienceToNextLevel);
        Assert.Equal((2, 0L), await StoredAsync(context, characterId));
        var audit = await AuditAsync(context, turnedIn.Value!.OperationId, "quest_turned_in");
        Assert.Equal(100, audit.GetProperty("rewardExperience").GetInt64());
        Assert.Equal(100, audit.GetProperty("experienceGained").GetInt64());

        await EconomyAudit.AssertCleanAsync(context);
    }

    [PostgresIntegrationFact]
    public async Task AKillCreditAtTheHighestLevelGainsNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "experience_master");
        var highest = context.Services.GetRequiredService<ProgressionRules>().MaximumCharacterLevel;
        var set = await RouteAsync(context, session, "admin_set_character_level", null, null, level: highest);

        var kill = await KillAsync(context, session, highest, 100);

        Assert.True(set.Succeeded, set.Error?.Message);
        Assert.Equal((highest, 0L, 0L, 0), Summary(ProgressionOf(kill)));
        Assert.Equal(0, ProgressionOf(kill).ExperienceToNextLevel);
        Assert.Equal((highest, 0L), await StoredAsync(context, player.Character.Id));

        await EconomyAudit.AssertCleanAsync(context);
    }

    [PostgresIntegrationFact]
    public async Task TheAdminToolsSetALevelAndGrantExperience()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "experience_admin");
        var characterId = player.Character.Id;
        var highest = context.Services.GetRequiredService<ProgressionRules>().MaximumCharacterLevel;
        await KillAsync(context, session, 1, 100);

        var raised = await RouteAsync(context, session, "admin_set_character_level", null, null, level: 5);
        var granted = await RouteAsync(context, session, "admin_grant_experience", null, null, experience: 30);
        var lowered = await RouteAsync(context, session, "admin_set_character_level", null, null, level: 2);
        ServiceResult<SimulationItemOperationResponse>[] refused =
        [
            await RouteAsync(context, session, "admin_set_character_level", null, null),
            await RouteAsync(context, session, "admin_set_character_level", null, null, level: 0),
            await RouteAsync(context, session, "admin_set_character_level", null, null, level: highest + 1),
            await RouteAsync(context, session, "admin_set_character_level", Wolves, null, level: 3),
            await RouteAsync(context, session, "admin_grant_experience", null, null),
            await RouteAsync(context, session, "admin_grant_experience", null, null, experience: 0),
            await RouteAsync(context, session, "admin_grant_experience", null, null, experience: 1_000_000_000_001L)
        ];

        Assert.Equal((5, 0L, 0L, 4), Summary(ProgressionOf(raised)));
        Assert.Equal((5, 30L, 30L, 0), Summary(ProgressionOf(granted)));
        Assert.Equal((2, 0L, 0L, -3), Summary(ProgressionOf(lowered)));
        Assert.All(refused, result => Assert.Equal(
            (400, ItemApiErrorCodes.SimulationOperationInvalid),
            (result.StatusCode, result.Error!.Code)));
        Assert.Equal((2, 0L), await StoredAsync(context, characterId));

        await EconomyAudit.AssertCleanAsync(context);
    }

    [PostgresIntegrationFact]
    public async Task OnlyADevelopmentGameServiceRunsTheProgressionTools()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var (_, player, session) = await JoinAsync(context, "experience_outsider");
        await using var scope = context.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var release = new SimulationQuestMutationService(
            provider.GetRequiredService<IItemTransactions>(),
            provider.GetRequiredService<SimulationCharacterStates>(),
            provider.GetRequiredService<QuestsModuleOptions>() with { DevelopmentQuestTools = false },
            provider.GetRequiredService<IQuestDefinitions>(),
            provider.GetRequiredService<ICharacterProgression>());

        var setLevel = await release.ExecuteAsync(
            session.WorkerId,
            session.SimulationSessionId,
            Request(session, "admin_set_character_level") with { Level = 5 },
            CancellationToken.None);
        var grant = await release.ExecuteAsync(
            session.WorkerId,
            session.SimulationSessionId,
            Request(session, "admin_grant_experience") with { Experience = 30 },
            CancellationToken.None);

        Assert.Equal((403, QuestTransactionErrorCodes.DevelopmentToolsDisabled), (setLevel.StatusCode, setLevel.Error!.Code));
        Assert.Equal((403, QuestTransactionErrorCodes.DevelopmentToolsDisabled), (grant.StatusCode, grant.Error!.Code));
        Assert.Equal((1, 0L), await StoredAsync(context, player.Character.Id));

        await EconomyAudit.AssertCleanAsync(context);
    }

    private static async Task<(ItemScenarioWorld World, IntegrationPlayer Player, ConsumedSimulationJoinTicketResponse Session)> JoinAsync(
        PostgresIntegrationTestContext context,
        string name)
    {
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync(name);
        return (world, player, await world.JoinAsync(player));
    }

    private static Task<ServiceResult<SimulationItemOperationResponse>> KillAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        int mobLevel,
        int mobExperiencePercent,
        Guid? operationId = null)
    {
        return RouteAsync(
            context,
            session,
            "credit_mob_kill",
            null,
            null,
            mobActorId: Wolf,
            mobTags: ["wolf"],
            mobLevel: mobLevel,
            mobExperiencePercent: mobExperiencePercent,
            operationId: operationId);
    }

    private static SimulationQuestOperationRequest Request(ConsumedSimulationJoinTicketResponse session, string operationKind)
    {
        return new SimulationQuestOperationRequest(
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            operationKind,
            null,
            QuestCatalogRevision: PostgresIntegrationTestContext.FixtureQuestCatalogRevision);
    }

    // The progression section as the worker item response passes it through.
    private static ItemTransactionProgressionChange ProgressionOf(ServiceResult<SimulationItemOperationResponse> result)
    {
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.True(result.Value!.Sections!.TryGetValue(ProgressionSection.Name, out var section));
        return section.Deserialize<ItemTransactionProgressionChange>(ItemOperationJson.Options)!;
    }

    private static (int Level, long Experience, long ExperienceGained, int LevelsGained) Summary(
        ItemTransactionProgressionChange change)
    {
        return (change.Level, change.Experience, change.ExperienceGained, change.LevelsGained);
    }

    private static async Task<(int Level, long Experience)> StoredAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<(int, long)>(
            "select level, experience from characters where id = @Id;",
            new { Id = characterId });
    }

    private static async Task<JsonElement> AuditAsync(
        PostgresIntegrationTestContext context,
        Guid operationId,
        string changeKind)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var json = await connection.QuerySingleAsync<string>(
            """
            select after_state::text
            from item_operation_changes
            where operation_id = @OperationId
              and change_kind = @ChangeKind;
            """,
            new { OperationId = operationId, ChangeKind = changeKind });
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
