using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Server.Common.Quests;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Quests.Tests;

// A quest whose minimum level is above the highest character level could never be accepted, so
// the quest seeder refuses the catalog at startup, before it reads the database.
public sealed class QuestCatalogLevelStartupTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task TheStartRefusesAQuestAboveTheHighestLevel()
    {
        await using var dataSource = UnreachableDataSource();
        await using var services = GameServiceTestComposition.BuildServiceProvider(dataSource);
        var progression = services.GetRequiredService<ICharacterProgression>();
        var seeder = Seeder(dataSource, progression, progression.MaximumLevel + 1);

        foreach (var start in new Func<Task>[] { () => seeder.PlanAsync(None), () => seeder.SeedAsync(None) })
        {
            var exception = await Assert.ThrowsAsync<QuestCatalogCompatibilityException>(start);
            Assert.Contains($"highest level {progression.MaximumLevel}", exception.Message, StringComparison.Ordinal);
            Assert.Contains(
                $"quest.fixture.errand requires level {progression.MaximumLevel + 1}",
                exception.Message,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AQuestAtTheHighestLevelPassesTheCheck()
    {
        await using var dataSource = UnreachableDataSource();
        await using var services = GameServiceTestComposition.BuildServiceProvider(dataSource);
        var progression = services.GetRequiredService<ICharacterProgression>();
        var seeder = Seeder(dataSource, progression, progression.MaximumLevel);

        // The check passes, so the plan goes on to the database, which does not exist.
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => seeder.PlanAsync(None));
    }

    // The seeder trusts the loaded catalog, so the test raises one quest's minimum level directly.
    private static QuestCatalogSeeder Seeder(
        NpgsqlDataSource dataSource,
        ICharacterProgression progression,
        int errandMinimumLevel)
    {
        var catalog = QuestCatalogFileLoader.Load(RepositoryPaths.QuestCatalogFixtureRuntimePath);
        catalog.Quests.Single(quest => quest.Id == "quest.fixture.errand").MinimumLevel = errandMinimumLevel;
        return new QuestCatalogSeeder(
            dataSource,
            new QuestDefinitions(catalog),
            new CharacterQuestLog(),
            progression,
            NullLogger<QuestCatalogSeeder>.Instance);
    }

    private static NpgsqlDataSource UnreachableDataSource()
    {
        return NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
    }
}
