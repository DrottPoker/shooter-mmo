using Mmorpg.WorldData.Content;

namespace Mmorpg.Tools.ContentPipeline;

// Command line shared by the content compilers. Without explicit roots the tools read the
// repository content directory and write the generated World data package folder.
public sealed class ContentToolOptions
{
    public const string Usage =
        "[--content-root <directory>] [--generated-root <directory>] [--verify]";

    private ContentToolOptions(string contentRoot, string generatedRoot, bool verifyOnly)
    {
        ContentRoot = contentRoot;
        GeneratedRoot = generatedRoot;
        VerifyOnly = verifyOnly;
    }

    public string ContentRoot { get; }

    public string GeneratedRoot { get; }

    public bool VerifyOnly { get; }

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out ContentToolOptions? options,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        options = null;
        string? contentRoot = null;
        string? generatedRoot = null;
        var verifyOnly = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--verify" when !verifyOnly:
                    verifyOnly = true;
                    break;
                case "--content-root" when contentRoot is null:
                    if (!TryReadDirectory(arguments, ref index, out contentRoot, out error))
                    {
                        return false;
                    }

                    break;
                case "--generated-root" when generatedRoot is null:
                    if (!TryReadDirectory(arguments, ref index, out generatedRoot, out error))
                    {
                        return false;
                    }

                    break;
                default:
                    error = $"Argument '{argument}' is unknown or repeated.";
                    return false;
            }
        }

        if (contentRoot is null || generatedRoot is null)
        {
            if (!TryFindRepositoryRoot(out var repositoryRoot, out error))
            {
                return false;
            }

            contentRoot ??= ContentLayout.ContentRoot(repositoryRoot);
            generatedRoot ??= ContentLayout.GeneratedRoot(repositoryRoot);
        }

        options = new ContentToolOptions(contentRoot, generatedRoot, verifyOnly);
        error = string.Empty;
        return true;
    }

    private static bool TryReadDirectory(
        IReadOnlyList<string> arguments,
        ref int index,
        out string? directory,
        out string error)
    {
        var name = arguments[index];
        if (index + 1 >= arguments.Count
            || string.IsNullOrWhiteSpace(arguments[index + 1])
            || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            directory = null;
            error = $"{name} requires a directory.";
            return false;
        }

        index++;
        directory = Path.GetFullPath(arguments[index]);
        error = string.Empty;
        return true;
    }

    private static bool TryFindRepositoryRoot(out string repositoryRoot, out string error)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            try
            {
                repositoryRoot = ContentLayout.FindRepositoryRoot(start);
                error = string.Empty;
                return true;
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        repositoryRoot = string.Empty;
        error = "The repository root was not found. Pass --content-root and --generated-root.";
        return false;
    }
}
