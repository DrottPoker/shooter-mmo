namespace Mmorpg.Tools.ProtocolGenerator;

// Command line of the generator. Without --repository-root it searches upward from the
// working directory and the tool directory for Mmorpg.slnx.
public sealed class ProtocolGeneratorOptions
{
    public const string Usage = "[--repository-root <directory>] [--verify]";

    private ProtocolGeneratorOptions(string repositoryRoot, bool verifyOnly)
    {
        RepositoryRoot = repositoryRoot;
        VerifyOnly = verifyOnly;
    }

    public string RepositoryRoot { get; }

    public bool VerifyOnly { get; }

    public static ProtocolGeneratorOptions Create(string repositoryRoot, bool verifyOnly)
    {
        return new ProtocolGeneratorOptions(Path.GetFullPath(repositoryRoot), verifyOnly);
    }

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out ProtocolGeneratorOptions? options,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        options = null;
        string? repositoryRoot = null;
        var verifyOnly = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--verify" when !verifyOnly:
                    verifyOnly = true;
                    break;
                case "--repository-root" when repositoryRoot is null:
                    if (index + 1 >= arguments.Count
                        || string.IsNullOrWhiteSpace(arguments[index + 1])
                        || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        error = "--repository-root requires a directory.";
                        return false;
                    }

                    repositoryRoot = Path.GetFullPath(arguments[++index]);
                    break;
                default:
                    error = $"Argument '{arguments[index]}' is unknown or repeated.";
                    return false;
            }
        }

        if (repositoryRoot is null && !TryFindRepositoryRoot(out repositoryRoot))
        {
            error = "The repository root was not found. Pass --repository-root.";
            return false;
        }

        options = new ProtocolGeneratorOptions(repositoryRoot!, verifyOnly);
        error = string.Empty;
        return true;
    }

    private static bool TryFindRepositoryRoot(out string? repositoryRoot)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, ProtocolLayout.SolutionFileName)))
                {
                    repositoryRoot = directory.FullName;
                    return true;
                }
            }
        }

        repositoryRoot = null;
        return false;
    }
}
