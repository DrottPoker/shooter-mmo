using Mmorpg.WorldData.Content;

namespace Mmorpg.Testing;

// Repository locations for tests that read authored content and generated World data.
public static class RepositoryPaths
{
    public static string Root { get; } = ContentLayout.FindRepositoryRoot(AppContext.BaseDirectory);

    public static string Content => ContentLayout.ContentRoot(Root);

    public static string Generated => ContentLayout.GeneratedRoot(Root);

    public static string Worlds => ContentLayout.WorldsDirectory(Content);
}
