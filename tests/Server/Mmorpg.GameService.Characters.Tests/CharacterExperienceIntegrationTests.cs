using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Testing;
using Mmorpg.Server.Common.Progression;
using Mmorpg.Testing;
using Mmorpg.WorldData.Progression;

namespace Mmorpg.GameService.Characters.Tests;

// Experience by the progression rules of the content: level 1 needs 100, level 2 150, level 3
// 210, and level 4 280. A grant levels up as far as it reaches, stops at the highest level, and
// a kill is worth what the rules give for the level that the character has under the lock.
public sealed class CharacterExperienceIntegrationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresIntegrationFact]
    public async Task AGrantWithinALevelAddsToTheLevelsExperience()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var change = await GrantAsync(context, player.Character.Id, 30);

        Assert.Equal(new CharacterProgressionSnapshot(1, 0, 100, Rules(context).HighestMobLevelWithoutExperience(1)), change.Before);
        Assert.Equal((1, 30L, 100L, 30L), (change.After.Level, change.After.Experience, change.After.ExperienceToNextLevel, change.Gained));
        Assert.Equal((1, 30L), await StoredAsync(context, player.Character.Id));
    }

    [PostgresIntegrationFact]
    public async Task AGrantLevelsUpAcrossOneAndSeveralLevels()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var first = await context.RegisterPlayerAsync("one@example.com", "one_level", "One Level");
        var second = await context.RegisterPlayerAsync("many@example.com", "many_levels", "Many Levels");

        var one = await GrantAsync(context, first.Character.Id, 120);
        var several = await GrantAsync(context, second.Character.Id, 500);

        Assert.Equal((2, 20L, 150L, 120L), (one.After.Level, one.After.Experience, one.After.ExperienceToNextLevel, one.Gained));
        Assert.Equal((4, 40L, 280L, 500L), (several.After.Level, several.After.Experience, several.After.ExperienceToNextLevel, several.Gained));
        Assert.Equal((4, 40L), await StoredAsync(context, second.Character.Id));
    }

    [PostgresIntegrationFact]
    public async Task AGrantStopsAtTheHighestLevelAndTheHighestLevelGainsNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var characterId = player.Character.Id;
        var rules = Rules(context);
        var highest = rules.MaximumCharacterLevel;
        Assert.True(await SetLevelAsync(context, characterId, highest - 1));

        var reached = await GrantAsync(context, characterId, 1_000_000_000_000L);
        var stays = await GrantAsync(context, characterId, 500);

        Assert.Equal(
            new CharacterProgressionSnapshot(highest, 0, 0, int.MaxValue),
            reached.After);
        Assert.Equal(rules.ExperienceToNextLevel(highest - 1), reached.Gained);
        Assert.Equal((reached.After, reached.After, 0L), (stays.Before, stays.After, stays.Gained));
        Assert.Equal((highest, 0L), await StoredAsync(context, characterId));
    }

    [PostgresIntegrationFact]
    public async Task AGrantOfNothingOnlyReadsAndANegativeOneIsRefused()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var nothing = await GrantAsync(context, player.Character.Id, 0);

        Assert.Equal((nothing.Before, 0L), (nothing.After, nothing.Gained));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GrantAsync(context, player.Character.Id, -1));
        await Assert.ThrowsAsync<ArgumentException>(() => InTransactionAsync(context, (progression, scope) =>
            progression.GrantExperienceAsync(scope, player.Character.Id, 10, CharacterExperienceSources.Kill, None)));
    }

    // Level 87 counts Mobs down to 21 levels below for their whole experience and nothing from 43
    // levels below, so a level 66 Mob gives its plain 345 and a level 44 Mob, the highest that
    // gives nothing, gives nothing.
    [PostgresIntegrationFact]
    public async Task KillExperienceUsesTheCharactersLevelUnderTheLock()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var characterId = player.Character.Id;
        var rules = Rules(context);
        Assert.True(await SetLevelAsync(context, characterId, 87));

        var near = await KillAsync(context, characterId, 66, 100);
        var far = await KillAsync(context, characterId, 44, 100);
        var none = await KillAsync(context, characterId, 66, 0);

        Assert.Equal(345L, rules.KillExperience(87, 66, 100));
        Assert.Equal(345L, near.Gained);
        Assert.Equal((87, 345L), (near.After.Level, near.After.Experience));
        Assert.Equal((0L, near.After), (far.Gained, far.After));
        Assert.Equal((0L, near.After), (none.Gained, none.After));
        Assert.Equal(44, near.After.HighestMobLevelWithoutExperience);
    }

    [PostgresIntegrationFact]
    public async Task SettingALevelSetsTheExperienceToNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await GrantAsync(context, player.Character.Id, 30);

        Assert.True(await SetLevelAsync(context, player.Character.Id, 3));

        Assert.Equal((3, 0L), await StoredAsync(context, player.Character.Id));
        Assert.Equal(
            new CharacterProgressionSnapshot(3, 0, 210, Rules(context).HighestMobLevelWithoutExperience(3)),
            await ReadAsync(context, player.Character.Id));
    }

    [PostgresIntegrationFact]
    public async Task AMissingCharacterGivesNull()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var missing = Guid.NewGuid();

        Assert.Null(await ReadAsync(context, missing));
        Assert.Null(await InTransactionAsync(context, (progression, scope) =>
            progression.GrantExperienceAsync(scope, missing, 10, CharacterExperienceSources.Quest, None)));
        Assert.Null(await InTransactionAsync(context, (progression, scope) =>
            progression.GrantKillExperienceAsync(scope, missing, 1, 100, None)));
        Assert.False(await SetLevelAsync(context, missing, 2));
    }

    // A lowered curve can leave more stored than the level needs. The snapshot shows the level one
    // short of full, and the next grant levels up with the surplus.
    [PostgresIntegrationFact]
    public async Task TheSnapshotCapsAStoredSurplusAndTheNextGrantLevelsUpWithIt()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var characterId = player.Character.Id;
        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "update characters set experience = 1000 where id = @Id;",
                new { Id = characterId });
        }

        var read = await ReadAsync(context, characterId);
        var change = await GrantAsync(context, characterId, 1);

        Assert.Equal((1, 99L, 100L), (read!.Level, read.Experience, read.ExperienceToNextLevel));
        Assert.Equal(read, change.Before);
        var expected = Rules(context).Add(new ProgressionState(1, 1000), 1);
        Assert.Equal((5, 261L), (expected.Level, expected.Experience));
        Assert.Equal((expected.Level, expected.Experience, 1L), (change.After.Level, change.After.Experience, change.Gained));
        Assert.Equal((5, 261L), await StoredAsync(context, characterId));
    }

    // A highest level lowered below a stored level leaves the character as stored: it gains
    // nothing and reads as a character at the highest level.
    [PostgresIntegrationFact]
    public async Task ACharacterAboveALoweredHighestLevelStaysAsStored()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var characterId = player.Character.Id;
        await StoreAsync(context, characterId, 50, 30);
        var lowered = ProgressionWithHighestLevel(20);

        var change = await InTransactionAsync(context, lowered, (progression, scope) =>
            progression.GrantExperienceAsync(scope, characterId, 100, CharacterExperienceSources.Quest, None));
        var read = await InTransactionAsync(context, lowered, (progression, scope) =>
            progression.ReadAsync(scope, characterId, None));

        var expected = new CharacterProgressionSnapshot(50, 0, 0, int.MaxValue);
        Assert.NotNull(change);
        Assert.Equal((expected, expected, 0L), (change.Before, change.After, change.Gained));
        Assert.Equal(expected, read);
        Assert.Equal((50, 30L), await StoredAsync(context, characterId));
    }

    // With the highest level at 3, level 1 needs 100 and level 2 150, so 300 stored at level 1
    // already reaches it: a grant of 1 levels up to it with the surplus, and counts nothing.
    [PostgresIntegrationFact]
    public async Task AStoredSurplusThatReachesTheHighestLevelLevelsUpToItWithAnyGrant()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var characterId = player.Character.Id;
        await StoreAsync(context, characterId, 1, 300);
        var lowered = ProgressionWithHighestLevel(3);

        var change = await InTransactionAsync(context, lowered, (progression, scope) =>
            progression.GrantExperienceAsync(scope, characterId, 1, CharacterExperienceSources.Quest, None));

        Assert.NotNull(change);
        Assert.Equal((1, 99L, 100L), (change.Before.Level, change.Before.Experience, change.Before.ExperienceToNextLevel));
        Assert.Equal(new CharacterProgressionSnapshot(3, 0, 0, int.MaxValue), change.After);
        Assert.Equal(0L, change.Gained);
        Assert.Equal((3, 0L), await StoredAsync(context, characterId));
    }

    // The content's rules with another highest level, for a progression outside the service.
    private static CharacterProgression ProgressionWithHighestLevel(int highestLevel)
    {
        var document = JsonNode.Parse(File.ReadAllText(RepositoryPaths.ProgressionRulesPath))!;
        document["maximumCharacterLevel"] = highestLevel;
        return new CharacterProgression(
            ProgressionRulesFileLoader.Parse(document.ToJsonString()),
            new CharacterProgressionMetrics());
    }

    private static async Task StoreAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        int level,
        long experience)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "update characters set level = @Level, experience = @Experience where id = @Id;",
            new { Id = characterId, Level = level, Experience = experience });
    }

    private static ProgressionRules Rules(PostgresIntegrationTestContext context)
    {
        return context.Services.GetRequiredService<ProgressionRules>();
    }

    private static async Task<CharacterExperienceChange> GrantAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        long experience)
    {
        var change = await InTransactionAsync(context, (progression, scope) =>
            progression.GrantExperienceAsync(scope, characterId, experience, CharacterExperienceSources.Quest, None));
        Assert.NotNull(change);
        return change;
    }

    private static async Task<CharacterExperienceChange> KillAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        int mobLevel,
        int mobExperiencePercent)
    {
        var change = await InTransactionAsync(context, (progression, scope) =>
            progression.GrantKillExperienceAsync(scope, characterId, mobLevel, mobExperiencePercent, None));
        Assert.NotNull(change);
        return change;
    }

    private static Task<bool> SetLevelAsync(PostgresIntegrationTestContext context, Guid characterId, int level)
    {
        return InTransactionAsync(context, (progression, scope) =>
            progression.SetLevelAsync(scope, characterId, level, None));
    }

    private static Task<CharacterProgressionSnapshot?> ReadAsync(PostgresIntegrationTestContext context, Guid characterId)
    {
        return InTransactionAsync(context, (progression, scope) => progression.ReadAsync(scope, characterId, None));
    }

    private static Task<T> InTransactionAsync<T>(
        PostgresIntegrationTestContext context,
        Func<ICharacterProgression, PostgresTransactionScope, Task<T>> action)
    {
        return InTransactionAsync(context, context.Services.GetRequiredService<ICharacterProgression>(), action);
    }

    private static async Task<T> InTransactionAsync<T>(
        PostgresIntegrationTestContext context,
        ICharacterProgression progression,
        Func<ICharacterProgression, PostgresTransactionScope, Task<T>> action)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await action(progression, new PostgresTransactionScope(connection, transaction));
        await transaction.CommitAsync();
        return result;
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
}
