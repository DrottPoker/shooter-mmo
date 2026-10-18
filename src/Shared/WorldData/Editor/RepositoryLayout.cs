using System.IO;
using Mmorpg.WorldData.Content;
using UnityEngine;

namespace Mmorpg.WorldData.Editor
{
    // Repository locations used by the Unity content tools. The root is found from the
    // solution marker, so the tools keep working wherever the Unity project lives.
    public static class RepositoryLayout
    {
        public static string FindRoot()
        {
            return ContentLayout.FindRepositoryRoot(Application.dataPath);
        }

        public static string ContentRoot(string repositoryRoot)
        {
            return ContentLayout.ContentRoot(repositoryRoot);
        }

        public static string GeneratedRoot(string repositoryRoot)
        {
            return ContentLayout.GeneratedRoot(repositoryRoot);
        }

        public static string CollisionSourcePath(string repositoryRoot, string worldId)
        {
            return ContentLayout.CollisionSourcePath(ContentRoot(repositoryRoot), worldId);
        }

        public static string GeneratedCollisionDirectory(string repositoryRoot, string worldId)
        {
            return ContentLayout.GeneratedCollisionDirectory(GeneratedRoot(repositoryRoot), worldId);
        }

        public static string ToolProjectPath(string repositoryRoot, string toolName)
        {
            return Path.Combine(repositoryRoot, "tools", toolName, "Mmorpg.Tools." + toolName + ".csproj");
        }

        public static string GameServiceDirectory(string repositoryRoot)
        {
            return Path.Combine(repositoryRoot, "src", "Server", "GameService");
        }

        public static string GameServiceProjectPath(string repositoryRoot)
        {
            return Path.Combine(GameServiceDirectory(repositoryRoot), "Mmorpg.GameService.csproj");
        }
    }
}
