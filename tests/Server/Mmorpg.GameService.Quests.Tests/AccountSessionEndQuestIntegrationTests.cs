using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using static Mmorpg.GameService.Quests.Tests.QuestScenario;

namespace Mmorpg.GameService.Quests.Tests;

// Once the account session of a simulation session ended, the kills of the character's body still
// count for its quests and the worker still reads its quest log, while a quest action that a
// client asks for is refused with the code of the end.
public sealed class AccountSessionEndQuestIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task AfterALogoutTheBodysKillsCountAndAQuestActionIsRefused()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("ended_hunter");
        var session = await world.JoinAsync(player);
        ItemScenarioWorld.AssertCommitted(await ExecuteAsync(context, session, new AcceptQuestCommand(player.Character.Id, Wolves), Mira));
        var logout = await context.SessionService.RevokeAsync(
            player.Registration.AccountId,
            player.Registration.SessionId,
            AccountSessionRevocationReason.Logout,
            CancellationToken.None);
        Assert.True(logout.Succeeded, logout.Error?.Message);

        var kill = await RouteAsync(context, session, "credit_quest_kill", null, null, mobActorId: "mob.feral_wolf", mobTags: ["wolf"]);
        var abandon = await RouteAsync(context, session, "abandon_quest", Wolves, Mira);
        await using var scope = context.Services.CreateAsyncScope();
        var log = await scope.ServiceProvider.GetRequiredService<SimulationQuestLogService>().ReadAsync(
            session.WorkerId,
            session.SimulationSessionId,
            new SimulationQuestLogRequest(
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                PostgresIntegrationTestContext.FixtureQuestCatalogRevision),
            CancellationToken.None);

        Assert.True(kill.Succeeded, kill.Error?.Message);
        Assert.Equal(1, Assert.Single(QuestLogOf(kill.Value!)!.Quests).Objectives.Single().Progress);
        Assert.Equal((401, ItemTransactionErrorCodes.AccountSessionEnded), (abandon.StatusCode, abandon.Error!.Code));
        Assert.True(log.Succeeded, log.Error?.Message);
        Assert.Contains(log.Value!.ActiveQuests, quest => quest.QuestId == Wolves);
    }
}
