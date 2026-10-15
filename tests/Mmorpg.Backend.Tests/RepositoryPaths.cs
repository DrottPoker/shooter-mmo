namespace Mmorpg.Backend.Tests;

// Repository locations for tests that read committed content. The root is found from
// the solution marker above the test output folder.
internal static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    public static string WorldData => Path.Combine(Root, "src", "Shared", "WorldData");

    public static string SharedContent => Path.Combine(WorldData, "Shared");

    public static string Worlds => Path.Combine(WorldData, "Worlds");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mmorpg.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MMORPG repository root.");
    }
}
