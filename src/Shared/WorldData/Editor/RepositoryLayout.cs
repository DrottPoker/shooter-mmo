using System.IO;
using UnityEngine;

namespace Mmorpg.WorldData.Editor
{
    // Repository locations used by the Unity content tools. The root is found from the
    // solution marker, so the tools keep working wherever the Unity project lives.
    public static class RepositoryLayout
    {
        public const string SolutionFileName = "Mmorpg.slnx";

        public static string FindRoot()
        {
            var directory = new DirectoryInfo(Application.dataPath);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not locate " + SolutionFileName + " above the Unity project.");
        }

        public static string WorldDataDirectory(string repositoryRoot)
        {
            return Path.Combine(repositoryRoot, "src", "Shared", "WorldData");
        }

        public static string SharedContentDirectory(string repositoryRoot)
        {
            return Path.Combine(WorldDataDirectory(repositoryRoot), "Shared");
        }

        public static string WorldsDirectory(string repositoryRoot)
        {
            return Path.Combine(WorldDataDirectory(repositoryRoot), "Worlds");
        }

        public static string ToolProjectPath(string repositoryRoot, string toolName)
        {
            return Path.Combine(repositoryRoot, "tools", toolName, "Mmorpg.Tools." + toolName + ".csproj");
        }

        public static string GameServiceDirectory(string repositoryRoot)
        {
            return Path.Combine(repositoryRoot, "src", "Server", "GameService");
        }
    }
}
