using Mmorpg.GameService.Content;
using Mmorpg.GameService.Testing;
using Mmorpg.WorldData.Text;

namespace Mmorpg.GameService.Content.Tests;

// A temporary copy of the files the test kit serves, so a test can break one of them.
internal sealed class ContentTestFiles : IDisposable
{
    private readonly string root;

    public ContentTestFiles()
    {
        var source = GameServiceTestComposition.CreateContentOptions();
        root = Path.Combine(Path.GetTempPath(), "mmorpg-content-tests-" + Guid.NewGuid().ToString("N"));
        TextTablesPath = Path.Combine(root, "Text", TextTableFormat.SourceLocale);
        Directory.CreateDirectory(TextTablesPath);
        ItemCatalogPath = Path.Combine(root, "core.item-catalog.json");
        QuestCatalogPath = Path.Combine(root, "core.quest-catalog.json");
        File.Copy(source.ItemCatalogPath, ItemCatalogPath);
        File.Copy(source.QuestCatalogPath, QuestCatalogPath);
        foreach (var path in Directory.EnumerateFiles(source.TextTablesPath, "*" + TextTableFormat.FileSuffix))
        {
            File.Copy(path, Path.Combine(TextTablesPath, Path.GetFileName(path)));
        }

        Options = source with
        {
            ItemCatalogPath = ItemCatalogPath,
            QuestCatalogPath = QuestCatalogPath,
            TextTablesPath = TextTablesPath
        };
    }

    public string ItemCatalogPath { get; }

    public string QuestCatalogPath { get; }

    public string TextTablesPath { get; }

    public ContentModuleOptions Options { get; }

    public string TextTablePath(string tableId)
    {
        return Path.Combine(TextTablesPath, tableId + TextTableFormat.FileSuffix);
    }

    // Replaces the first occurrence of a value in a file, keeping LF line endings.
    public static void ReplaceOnce(string path, string oldValue, string newValue)
    {
        var text = File.ReadAllText(path);
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{oldValue}' is not in '{path}'.");
        File.WriteAllText(path, text[..index] + newValue + text[(index + oldValue.Length)..]);
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }
}
