using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Quests.Tests;

// The quest log contract against PostgreSQL, with the quests of the fixture quest catalog.
public sealed class CharacterQuestLogIntegrationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresIntegrationFact]
    public async Task ANewCharacterHasAnEmptyQuestLog()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var log = await QuestLogTestDatabase.ReadAsync(context, QuestLog(context), player.Character.Id);

        Assert.Equal(0, log.Revision);
        Assert.Empty(log.ActiveQuests);
        Assert.Empty(log.CompletedQuests);
    }

    [PostgresIntegrationFact]
    public async Task EveryChangeRaisesTheRevisionAndTheLogKeepsTheCurrentStepsProgress()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var characterId = (await context.RegisterPlayerAsync()).Character.Id;
        var questLog = QuestLog(context);

        Assert.Equal(1, await QuestLogTestDatabase.WriteAsync(context, scope => questLog.AcceptAsync(
            scope, characterId, "quest.fixture.errand", "work", ["ore", "hand_over", "report"], None)));
        Assert.Equal(2, await QuestLogTestDatabase.WriteAsync(context, scope => questLog.SetProgressAsync(
            scope, characterId, "quest.fixture.errand", new Dictionary<string, int> { ["ore"] = 2, ["report"] = 1 }, None)));
        var working = Assert.Single((await QuestLogTestDatabase.ReadAsync(context, questLog, characterId)).ActiveQuests);
        Assert.Equal(("quest.fixture.errand", "work"), (working.QuestId, working.StepId));
        Assert.Equal(new Dictionary<string, int> { ["hand_over"] = 0, ["ore"] = 2, ["report"] = 1 }, working.Progress);

        // A collect step counts carried items, so it starts without stored progress.
        Assert.Equal(3, await QuestLogTestDatabase.WriteAsync(context, scope => questLog.AdvanceAsync(
            scope, characterId, "quest.fixture.errand", "return", [], None)));
        var returning = Assert.Single((await QuestLogTestDatabase.ReadAsync(context, questLog, characterId)).ActiveQuests);
        Assert.Equal("return", returning.StepId);
        Assert.Empty(returning.Progress);

        Assert.Equal(4, await QuestLogTestDatabase.WriteAsync(context, scope => questLog.CompleteAsync(
            scope, characterId, "quest.fixture.errand", None)));
        var completed = await QuestLogTestDatabase.ReadAsync(context, questLog, characterId);
        Assert.Equal(4, completed.Revision);
        Assert.Empty(completed.ActiveQuests);
        var completion = Assert.Single(completed.CompletedQuests);
        Assert.Equal(("quest.fixture.errand", 1), (completion.QuestId, completion.CompletionCount));
        Assert.Equal(0, await CountProgressRowsAsync(context, characterId));
    }

    [PostgresIntegrationFact]
    public async Task ARepeatedQuestCountsEveryCompletion()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var characterId = (await context.RegisterPlayerAsync()).Character.Id;
        var questLog = QuestLog(context);

        for (var run = 0; run < 2; run++)
        {
            await QuestLogTestDatabase.WriteAsync(context, scope => questLog.AcceptAsync(
                scope, characterId, "quest.fixture.ore_run", "deliver", [], None));
            await QuestLogTestDatabase.WriteAsync(context, scope => questLog.CompleteAsync(
                scope, characterId, "quest.fixture.ore_run", None));
        }

        var log = await QuestLogTestDatabase.ReadAsync(context, questLog, characterId);
        Assert.Equal(4, log.Revision);
        var completion = Assert.Single(log.CompletedQuests);
        Assert.Equal(("quest.fixture.ore_run", 2), (completion.QuestId, completion.CompletionCount));
    }

    [PostgresIntegrationFact]
    public async Task AbandoningDropsTheQuestWithItsProgressAndCountsNoCompletion()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var characterId = (await context.RegisterPlayerAsync()).Character.Id;
        var questLog = QuestLog(context);
        await QuestLogTestDatabase.WriteAsync(context, scope => questLog.AcceptAsync(
            scope, characterId, "quest.fixture.wolves", "hunt", ["wolves"], None));
        await QuestLogTestDatabase.WriteAsync(context, scope => questLog.SetProgressAsync(
            scope, characterId, "quest.fixture.wolves", new Dictionary<string, int> { ["wolves"] = 1 }, None));

        Assert.Equal(3, await QuestLogTestDatabase.WriteAsync(context, scope => questLog.AbandonAsync(
            scope, characterId, "quest.fixture.wolves", None)));

        var log = await QuestLogTestDatabase.ReadAsync(context, questLog, characterId);
        Assert.Empty(log.ActiveQuests);
        Assert.Empty(log.CompletedQuests);
        Assert.Equal(0, await CountProgressRowsAsync(context, characterId));
    }

    [PostgresIntegrationFact]
    public async Task AWriteThatDoesNotFitTheStoredStateFailsAndChangesNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var characterId = (await context.RegisterPlayerAsync()).Character.Id;
        var questLog = QuestLog(context);
        await QuestLogTestDatabase.WriteAsync(context, scope => questLog.AcceptAsync(
            scope, characterId, "quest.fixture.wolves", "hunt", ["wolves"], None));

        await Assert.ThrowsAsync<InvalidOperationException>(() => QuestLogTestDatabase.WriteAsync(context, scope =>
            questLog.SetProgressAsync(scope, characterId, "quest.fixture.wolves", new Dictionary<string, int> { ["ghosts"] = 1 }, None)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => QuestLogTestDatabase.WriteAsync(context, scope =>
            questLog.SetProgressAsync(scope, characterId, "quest.fixture.errand", new Dictionary<string, int> { ["ore"] = 1 }, None)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => QuestLogTestDatabase.WriteAsync(context, scope =>
            questLog.AbandonAsync(scope, characterId, "quest.fixture.errand", None)));
        await Assert.ThrowsAsync<PostgresException>(() => QuestLogTestDatabase.WriteAsync(context, scope =>
            questLog.AcceptAsync(scope, characterId, "quest.fixture.unknown", "hunt", [], None)));
        await Assert.ThrowsAsync<PostgresException>(() => QuestLogTestDatabase.WriteAsync(context, scope =>
            questLog.AcceptAsync(scope, characterId, "quest.fixture.wolves", "hunt", ["wolves"], None)));

        var log = await QuestLogTestDatabase.ReadAsync(context, questLog, characterId);
        Assert.Equal(1, log.Revision);
        Assert.Equal(new Dictionary<string, int> { ["wolves"] = 0 }, Assert.Single(log.ActiveQuests).Progress);
    }

    private static ICharacterQuestLog QuestLog(PostgresIntegrationTestContext context)
    {
        return context.Services.GetRequiredService<ICharacterQuestLog>();
    }

    private static async Task<long> CountProgressRowsAsync(PostgresIntegrationTestContext context, Guid characterId)
    {
        await using var command = context.DataSource.CreateCommand(
            "select count(*) from character_quest_progress where character_id = @CharacterId;");
        command.Parameters.AddWithValue("CharacterId", characterId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
