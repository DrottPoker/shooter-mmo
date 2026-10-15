namespace Mmorpg.Backend.Tests.Architecture;

// Maps every GameService source file to the module that owns it, so the architecture
// tests can check table and namespace boundaries before and after module extraction.
internal static class GameServiceSourceMap
{
    public const string Platform = "Platform";
    public const string Accounts = "Accounts";
    public const string Characters = "Characters";
    public const string Placement = "Placement";
    public const string Items = "Items";
    public const string Host = "Host";

    // Lower modules come first. A module may only depend on modules before it.
    public static IReadOnlyList<string> DependencyOrder { get; } =
        [Platform, Accounts, Characters, Placement, Items, Host];

    // Ordered by specificity: the first matching prefix decides the module.
    private static readonly (string Prefix, string Module)[] PathRules =
    [
        ("GameService.Platform/", Platform),
        ("GameService.Accounts/", Accounts),
        ("GameService.Characters/", Characters),
        ("GameService/Placement/", Placement),
        ("GameService/Database/Migrations/", Items),
        ("GameService/Items/", Items),
        ("GameService/", Host)
    ];

    public static string ServerRoot { get; } = Path.Combine(RepositoryPaths.Root, "src", "Server");

    // The host project and every GameService module project next to it.
    public static IReadOnlyList<SourceFile> LoadSourceFiles()
    {
        return Directory
            .EnumerateDirectories(ServerRoot, "GameService*")
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(ServerRoot, path).Replace('\\', '/'))
            .Where(path => !path.Contains("/bin/", StringComparison.Ordinal)
                && !path.Contains("/obj/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(path => new SourceFile(
                path,
                ModuleOf(path),
                File.ReadAllText(Path.Combine(ServerRoot, path))))
            .ToArray();
    }

    public static string ModuleOf(string relativePath)
    {
        foreach (var (prefix, module) in PathRules)
        {
            if (relativePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return module;
            }
        }

        throw new InvalidOperationException($"No module owns '{relativePath}'.");
    }

    public static int RankOf(string module)
    {
        var rank = DependencyOrder.ToList().IndexOf(module);
        return rank >= 0 ? rank : throw new InvalidOperationException($"Unknown module '{module}'.");
    }
}

internal sealed record SourceFile(string RelativePath, string Module, string Text);
