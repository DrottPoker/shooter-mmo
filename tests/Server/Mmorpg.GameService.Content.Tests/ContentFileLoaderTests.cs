using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Mmorpg.GameService.Content;
using Mmorpg.GameService.Testing;
using Mmorpg.Server.Common.Items;
using Mmorpg.Server.Common.Quests;
using Mmorpg.WorldData.Text;

namespace Mmorpg.GameService.Content.Tests;

public sealed class ContentFileLoaderTests
{
    [Fact]
    public void LoadsBothCatalogsAndEveryTextTableAsTheyAreOnDisk()
    {
        var options = GameServiceTestComposition.CreateContentOptions();

        var store = ContentFileLoader.Load(options);

        var item = Assert.Single(store.Files, file => file.Kind == ContentFileKinds.ItemCatalog);
        Assert.Equal("core", item.Id);
        Assert.Null(item.Locale);
        Assert.Equal(ItemCatalogFileLoader.Load(options.ItemCatalogPath).Revision, item.Revision);
        Assert.Equal(File.ReadAllBytes(options.ItemCatalogPath), item.Bytes);

        var quest = Assert.Single(store.Files, file => file.Kind == ContentFileKinds.QuestCatalog);
        Assert.Equal("core", quest.Id);
        Assert.Equal(QuestCatalogFileLoader.Load(options.QuestCatalogPath).Revision, quest.Revision);
        Assert.Equal(File.ReadAllBytes(options.QuestCatalogPath), quest.Bytes);

        var tablePaths = Directory.GetFiles(options.TextTablesPath, "*" + TextTableFormat.FileSuffix);
        var tables = store.Files.Where(file => file.Kind == ContentFileKinds.TextTable).ToArray();
        Assert.NotEmpty(tablePaths);
        Assert.Equal(tablePaths.Length, tables.Length);
        foreach (var path in tablePaths)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var tableId = document.RootElement.GetProperty("tableId").GetString();
            var table = Assert.Single(tables, file => file.Id == tableId);
            Assert.Equal(TextTableFormat.SourceLocale, table.Locale);
            Assert.Equal(document.RootElement.GetProperty("revision").GetString(), table.Revision);
            Assert.Equal(TextTableFormat.Version, table.FormatVersion);
            Assert.Equal(File.ReadAllBytes(path), table.Bytes);
        }
    }

    [Fact]
    public void RefusesAMissingCatalog()
    {
        using var files = new ContentTestFiles();
        File.Delete(files.QuestCatalogPath);

        var exception = Assert.Throws<InvalidOperationException>(() => ContentFileLoader.Load(files.Options));

        Assert.Contains(files.QuestCatalogPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAMissingTextTableFolder()
    {
        using var files = new ContentTestFiles();
        Directory.Delete(files.TextTablesPath, recursive: true);

        var exception = Assert.Throws<InvalidOperationException>(() => ContentFileLoader.Load(files.Options));

        Assert.Contains(files.TextTablesPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAFolderWithoutTextTables()
    {
        using var files = new ContentTestFiles();
        foreach (var path in Directory.GetFiles(files.TextTablesPath))
        {
            File.Delete(path);
        }

        var exception = Assert.Throws<InvalidOperationException>(() => ContentFileLoader.Load(files.Options));

        Assert.Contains("holds no", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesATextTableWhoseRevisionDoesNotMatchItsTexts()
    {
        using var files = new ContentTestFiles();
        var path = files.TextTablePath(TextTableIds.Ui);
        ContentTestFiles.ReplaceOnce(path, "\"text\": \"", "\"text\": \"Changed ");

        var exception = Assert.Throws<InvalidOperationException>(() => ContentFileLoader.Load(files.Options));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Contains("revision does not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesTwoTextTablesWithTheSameId()
    {
        using var files = new ContentTestFiles();
        File.Copy(
            files.TextTablePath(TextTableIds.Ui),
            Path.Combine(files.TextTablesPath, "copy" + TextTableFormat.FileSuffix));

        var exception = Assert.Throws<InvalidOperationException>(() => ContentFileLoader.Load(files.Options));

        Assert.Contains("'ui'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("listed 2 times", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesACatalogWhoseRevisionDoesNotDescribeItsContent()
    {
        using var files = new ContentTestFiles();
        var revision = ItemCatalogFileLoader.Load(files.ItemCatalogPath).Revision;
        ContentTestFiles.ReplaceOnce(files.ItemCatalogPath, revision, new string('0', revision.Length));

        var exception = Assert.Throws<InvalidOperationException>(() => ContentFileLoader.Load(files.Options));

        Assert.Contains(files.ItemCatalogPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ListsEveryProblemInOneError()
    {
        using var files = new ContentTestFiles();
        File.Delete(files.ItemCatalogPath);
        File.Delete(files.QuestCatalogPath);

        var exception = Assert.Throws<InvalidOperationException>(() => ContentFileLoader.Load(files.Options));

        Assert.Contains(files.ItemCatalogPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(files.QuestCatalogPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationUsesTheDefaultsWhenNoKeyIsSet()
    {
        var options = ContentModuleOptions.FromConfiguration(
            new ConfigurationBuilder().Build(),
            "items.json",
            "quests.json");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ContentModuleOptions.DefaultTextTablesPath)),
            options.TextTablesPath);
        Assert.Null(options.BaseUrl);
        Assert.Equal(ContentModuleOptions.DefaultRateLimitPermitLimit, options.RateLimitPermitLimit);
        Assert.Equal(
            TimeSpan.FromSeconds(ContentModuleOptions.DefaultRateLimitWindowSeconds),
            options.RateLimitWindow);
    }

    [Fact]
    public void ConfigurationNormalizesTheBaseUrl()
    {
        var options = ContentModuleOptions.FromConfiguration(
            Configuration(("Content:BaseUrl", "https://cdn.example.test/game/content/")),
            "items.json",
            "quests.json");

        Assert.Equal("https://cdn.example.test/game/content", options.BaseUrl);
    }

    [Theory]
    [InlineData("Content:BaseUrl", "/relative/path")]
    [InlineData("Content:BaseUrl", "ftp://cdn.example.test/content")]
    [InlineData("Content:BaseUrl", "https://cdn.example.test/content?version=1")]
    [InlineData("RateLimiting:Content:PermitLimit", "0")]
    [InlineData("RateLimiting:Content:WindowSeconds", "many")]
    public void ConfigurationRefusesAnInvalidValue(string key, string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ContentModuleOptions.FromConfiguration(
            Configuration((key, value)),
            "items.json",
            "quests.json"));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(entry =>
                new KeyValuePair<string, string?>(entry.Key, entry.Value)))
            .Build();
    }
}
