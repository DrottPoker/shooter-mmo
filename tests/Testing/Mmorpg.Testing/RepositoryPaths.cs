using System.Reflection;
using Mmorpg.WorldData.Content;

namespace Mmorpg.Testing;

// Repository locations for tests that read authored content and generated World data.
public static class RepositoryPaths
{
    // Stamped on every test assembly by tests/Directory.Build.props.
    public const string ProjectDirectoryMetadataKey = "Mmorpg.TestProjectDirectory";

    public static string Root { get; } = ContentLayout.FindRepositoryRoot(AppContext.BaseDirectory);

    public static string Content => ContentLayout.ContentRoot(Root);

    public static string Generated => ContentLayout.GeneratedRoot(Root);

    public static string Worlds => ContentLayout.WorldsDirectory(Content);

    // The test-owned item catalog. Rule tests use it, so content and balance changes never
    // break them; content tests still read the real catalog.
    public static string ItemCatalogFixtureContent =>
        Path.Combine(Root, "tests", "Fixtures", "ItemCatalog", "content");

    public static string ItemCatalogFixtureGenerated =>
        Path.Combine(Root, "tests", "Fixtures", "ItemCatalog", "generated");

    public static string ItemCatalogFixtureRuntimePath =>
        ContentLayout.GeneratedItemCatalogPath(ItemCatalogFixtureGenerated);

    // The test-owned actor spawn layout. Simulation tests use it, so the development
    // Worlds can be redesigned without breaking them; content tests read the real Worlds.
    public static string WorldActorFixtureSpawnsPath =>
        Path.Combine(Root, "tests", "Fixtures", "WorldActors", "actor-spawns.json");

    // The source folder of a test project, for files the project keeps next to its tests.
    // Compile-time paths are not used because ContinuousIntegrationBuild remaps them.
    public static string ProjectDirectory(Assembly testAssembly)
    {
        var relativePath = testAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => string.Equals(
                attribute.Key,
                ProjectDirectoryMetadataKey,
                StringComparison.Ordinal))
            ?.Value
            ?? throw new InvalidOperationException(
                $"{testAssembly.GetName().Name} has no {ProjectDirectoryMetadataKey} assembly metadata.");
        return Path.GetFullPath(Path.Combine(Root, relativePath));
    }
}
