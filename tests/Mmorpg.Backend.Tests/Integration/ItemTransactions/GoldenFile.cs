using System.Text;
using Mmorpg.Testing;

namespace Mmorpg.Backend.Tests.Integration.ItemTransactions;

// Compares text with a checked-in golden file. Set MMORPG_UPDATE_GOLDENS=1 to write the
// actual text instead; review every golden diff before committing it.
internal static class GoldenFile
{
    public const string UpdateVariable = "MMORPG_UPDATE_GOLDENS";

    public static string FolderPath { get; } = Path.Combine(
        RepositoryPaths.Root,
        "tests",
        "Mmorpg.Backend.Tests",
        "Golden",
        "ItemTransactions");

    public static void AssertMatches(string name, string actual)
    {
        var path = Path.Combine(FolderPath, name + ".txt");
        var normalized = actual.ReplaceLineEndings("\n").TrimEnd('\n') + "\n";
        if (string.Equals(
                Environment.GetEnvironmentVariable(UpdateVariable),
                "1",
                StringComparison.Ordinal))
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(path, normalized, new UTF8Encoding(false));
            return;
        }

        Assert.True(
            File.Exists(path),
            $"Golden file '{name}.txt' is missing. Run with {UpdateVariable}=1 to create it. Actual:\n{normalized}");
        var expected = File.ReadAllText(path);
        Assert.Equal(expected, normalized);
    }
}
