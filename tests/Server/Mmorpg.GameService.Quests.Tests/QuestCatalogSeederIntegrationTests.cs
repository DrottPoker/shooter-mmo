using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Server.Common.Quests;
using Mmorpg.Testing;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Quests.Tests;

// The quest catalog mirror at startup: what it stores, and the catalog changes that stored
// quest state depends on, which stop the start instead of breaking the stored progress.
public sealed class QuestCatalogSeederIntegrationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresIntegrationFact]
    public async Task TheStartMirrorsTheRevisionAndEveryQuestIdentity()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var definitions = context.Services.GetRequiredService<IQuestDefinitions>();

        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            definitions.Revision,
            await connection.QuerySingleAsync<string>(
                "select revision from quest_catalogs where catalog_id = 'core';"));
        Assert.Equal(
            definitions.Quests.Select(quest => $"{quest.Id} {quest.StructuralFingerprint} active"),
            await MirroredQuestsAsync(context));
    }

    [PostgresIntegrationFact]
    public async Task ARestructuredQuestThatNobodyHasActiveIsMirroredAgain()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var catalog = FixtureCatalog();
        Quest(catalog, "quest.fixture.wolves").StructuralFingerprint = new string('a', 64);
        catalog.Revision = new string('b', 64);

        await SeedAsync(context, catalog);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            new string('b', 64),
            await connection.QuerySingleAsync<string>("select revision from quest_catalogs where catalog_id = 'core';"));
        Assert.Contains($"quest.fixture.wolves {new string('a', 64)} active", await MirroredQuestsAsync(context));
    }

    [PostgresIntegrationFact]
    public async Task ARestructuredQuestThatACharacterHasActiveStopsTheStart()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await AcceptAsync(context, "quest.fixture.wolves", "hunt", ["wolves"]);
        var mirrored = await MirroredQuestsAsync(context);
        var catalog = FixtureCatalog();
        Quest(catalog, "quest.fixture.wolves").StructuralFingerprint = new string('a', 64);

        var exception = await Assert.ThrowsAsync<QuestCatalogCompatibilityException>(() => SeedAsync(context, catalog));

        Assert.Contains("'quest.fixture.wolves' changed its steps or objectives", exception.Message, StringComparison.Ordinal);
        Assert.Equal(mirrored, await MirroredQuestsAsync(context));
    }

    [PostgresIntegrationFact]
    public async Task ARemovedQuestMustBeRetiredAndNeverReturns()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var removed = FixtureCatalog();
        removed.Quests = removed.Quests.Where(quest => quest.Id != "quest.fixture.ore_run").ToArray();
        var retired = FixtureCatalog();
        retired.Quests = retired.Quests.Where(quest => quest.Id != "quest.fixture.ore_run").ToArray();
        retired.RetiredQuestIds = ["quest.fixture.ore_run"];

        var missing = await Assert.ThrowsAsync<QuestCatalogCompatibilityException>(() => SeedAsync(context, removed));
        await SeedAsync(context, retired);
        var reused = await Assert.ThrowsAsync<QuestCatalogCompatibilityException>(() => SeedAsync(context, FixtureCatalog()));

        Assert.Contains("'quest.fixture.ore_run' is missing from the quest catalog", missing.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"quest.fixture.ore_run {Quest(FixtureCatalog(), "quest.fixture.ore_run").StructuralFingerprint} retired",
            await MirroredQuestsAsync(context));
        Assert.Contains("Retired quest id 'quest.fixture.ore_run' cannot be reused", reused.Message, StringComparison.Ordinal);
    }

    [PostgresIntegrationFact]
    public async Task RetiringAQuestThatACharacterHasActiveStopsTheStart()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await AcceptAsync(context, "quest.fixture.ore_run", "deliver", []);
        var retired = FixtureCatalog();
        retired.Quests = retired.Quests.Where(quest => quest.Id != "quest.fixture.ore_run").ToArray();
        retired.RetiredQuestIds = ["quest.fixture.ore_run"];

        var exception = await Assert.ThrowsAsync<QuestCatalogCompatibilityException>(() => SeedAsync(context, retired));

        Assert.Contains("'quest.fixture.ore_run' is retired while characters have it active", exception.Message, StringComparison.Ordinal);
    }

    private static QuestCatalogRuntimeDocument FixtureCatalog()
    {
        return QuestCatalogFileLoader.Load(RepositoryPaths.QuestCatalogFixtureRuntimePath);
    }

    private static QuestDefinition Quest(QuestCatalogRuntimeDocument catalog, string questId)
    {
        return catalog.Quests.Single(quest => quest.Id == questId);
    }

    // The seeder trusts the loaded catalog, so a test may hand it changed hashes directly.
    private static Task SeedAsync(PostgresIntegrationTestContext context, QuestCatalogRuntimeDocument catalog)
    {
        return new QuestCatalogSeeder(
            context.DataSource,
            new QuestDefinitions(catalog),
            NullLogger<QuestCatalogSeeder>.Instance).SeedAsync(None);
    }

    private static async Task AcceptAsync(
        PostgresIntegrationTestContext context,
        string questId,
        string stepId,
        string[] objectiveIds)
    {
        var characterId = (await context.RegisterPlayerAsync()).Character.Id;
        var questLog = context.Services.GetRequiredService<ICharacterQuestLog>();
        await QuestLogTestDatabase.WriteAsync(context, scope => questLog.AcceptAsync(
            scope, characterId, questId, stepId, objectiveIds, None));
    }

    private static async Task<IReadOnlyList<string>> MirroredQuestsAsync(PostgresIntegrationTestContext context)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return (await connection.QueryAsync<string>(
                """
                select id || ' ' || structural_fingerprint || ' '
                       || case when retired_at is null then 'active' else 'retired' end
                from quest_definitions
                order by id;
                """))
            .ToArray();
    }
}
