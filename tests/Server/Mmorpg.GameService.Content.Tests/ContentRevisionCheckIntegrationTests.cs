using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameService.Content;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Content;

namespace Mmorpg.GameService.Content.Tests;

public sealed class ContentRevisionCheckIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task AcceptsTheCatalogsThatTheModulesMirrored()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var check = CreateCheck(context, ContentFileLoader.Load(GameServiceTestComposition.CreateContentOptions()));

        await check.StartAsync(CancellationToken.None);
    }

    [PostgresIntegrationFact]
    public async Task RefusesAnItemCatalogThatTheItemsModuleDoesNotRun()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        using var files = new ContentTestFiles();
        // The game's item catalog is valid but is not the fixture catalog the test database mirrors.
        File.Copy(ContentLayout.GeneratedItemCatalogPath(RepositoryPaths.Generated), files.ItemCatalogPath, overwrite: true);
        var check = CreateCheck(context, ContentFileLoader.Load(files.Options));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => check.StartAsync(CancellationToken.None));

        Assert.Contains("item-catalog", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Items module", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Quests module", exception.Message, StringComparison.Ordinal);
    }

    [PostgresIntegrationFact]
    public async Task RefusesAQuestCatalogThatTheQuestsModuleDoesNotRun()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        using var files = new ContentTestFiles();
        File.Copy(ContentLayout.GeneratedQuestCatalogPath(RepositoryPaths.Generated), files.QuestCatalogPath, overwrite: true);
        var check = CreateCheck(context, ContentFileLoader.Load(files.Options));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => check.StartAsync(CancellationToken.None));

        Assert.Contains("quest-catalog", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Quests module", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Items module", exception.Message, StringComparison.Ordinal);
    }

    private static ContentRevisionCheck CreateCheck(
        PostgresIntegrationTestContext context,
        ContentFileStore store)
    {
        return new ContentRevisionCheck(
            store,
            context.DataSource,
            context.Services.GetRequiredService<IItemCatalogRevisionProvider>(),
            context.Services.GetRequiredService<IQuestCatalogRevisionProvider>(),
            NullLogger<ContentRevisionCheck>.Instance);
    }
}
