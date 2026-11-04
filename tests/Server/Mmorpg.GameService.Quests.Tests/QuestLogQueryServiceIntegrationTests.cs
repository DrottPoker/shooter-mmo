using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Quests.Tests;

// GET /api/characters/{characterId}/quests: the owning account reads the quest log with
// the quest catalog revision; nobody else learns that the character exists.
public sealed class QuestLogQueryServiceIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task TheOwnerReadsTheQuestLogWithTheCatalogRevision()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var questLog = context.Services.GetRequiredService<ICharacterQuestLog>();
        await QuestLogTestDatabase.WriteAsync(context, scope => questLog.AcceptAsync(
            scope, player.Character.Id, "quest.fixture.wolves", "hunt", ["wolves"], CancellationToken.None));
        await QuestLogTestDatabase.WriteAsync(context, scope => questLog.AcceptAsync(
            scope, player.Character.Id, "quest.fixture.ore_run", "deliver", [], CancellationToken.None));
        await QuestLogTestDatabase.WriteAsync(context, scope => questLog.CompleteAsync(
            scope, player.Character.Id, "quest.fixture.ore_run", CancellationToken.None));

        var result = await QueryService(context).GetCharacterQuestLogAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var log = result.Value!;
        Assert.Equal(player.Character.Id, log.CharacterId);
        Assert.Equal(context.Services.GetRequiredService<IQuestDefinitions>().Revision, log.QuestCatalogRevision);
        Assert.Equal(3, log.QuestLogRevision);
        var active = Assert.Single(log.ActiveQuests);
        Assert.Equal(("quest.fixture.wolves", "hunt"), (active.QuestId, active.StepId));
        Assert.Equal([new QuestObjectiveProgressResponse("wolves", 0)], active.Objectives);
        var completed = Assert.Single(log.CompletedQuests);
        Assert.Equal(("quest.fixture.ore_run", 1), (completed.QuestId, completed.CompletionCount));
    }

    [PostgresIntegrationFact]
    public async Task AnotherAccountCannotReadTheQuestLog()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var owner = await context.RegisterPlayerAsync();
        var other = await context.RegisterPlayerAsync("other@example.com", "other_player", "Other Hero");

        var result = await QueryService(context).GetCharacterQuestLogAsync(
            other.Registration.AccountId,
            owner.Character.Id,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(404, result.StatusCode);
        Assert.Equal("character_quests_not_found", result.Error!.Code);
    }

    private static QuestLogQueryService QueryService(PostgresIntegrationTestContext context)
    {
        return context.Services.GetRequiredService<QuestLogQueryService>();
    }
}
