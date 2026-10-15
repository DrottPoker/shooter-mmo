using System.Reflection;
using System.Text;

namespace Mmorpg.Testing;

// Compares text with checked-in golden files in a folder of the test project. Set
// MMORPG_UPDATE_GOLDENS=1 to write the actual text instead; review every golden diff
// before committing it.
public sealed class GoldenFiles
{
    public const string UpdateVariable = "MMORPG_UPDATE_GOLDENS";

    private GoldenFiles(string folderPath)
    {
        FolderPath = folderPath;
    }

    public string FolderPath { get; }

    // A folder below the project directory of the given test assembly.
    public static GoldenFiles ForProject(Assembly testAssembly, params string[] folderSegments)
    {
        return new GoldenFiles(Path.Combine(
            [RepositoryPaths.ProjectDirectory(testAssembly), .. folderSegments]));
    }

    public void AssertMatches(string name, string actual)
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
