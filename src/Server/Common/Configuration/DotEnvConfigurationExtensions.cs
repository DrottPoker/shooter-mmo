using Microsoft.Extensions.Configuration;

namespace Mmorpg.Server.Common.Configuration;

public static class DotEnvConfigurationExtensions
{
    // Marks the repository root. A .env file is only loaded at or below this root.
    private const string RepositoryRootMarker = "Mmorpg.slnx";

    public static IConfigurationBuilder AddOptionalDotEnvFile(
        this IConfigurationBuilder configuration,
        params string[] searchRoots)
    {
        var path = searchRoots
            .Select(FindDotEnvFile)
            .FirstOrDefault(candidate => candidate is not null);
        if (path is null)
        {
            return configuration;
        }

        configuration.AddInMemoryCollection(Parse(path, File.ReadLines(path)));
        return configuration;
    }

    public static IReadOnlyDictionary<string, string?> Parse(
        string sourceName,
        IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                // Never echo the line, because it may contain a secret.
                throw new InvalidOperationException(
                    $"Invalid .env entry in {sourceName} on line {lineNumber}. Expected KEY=value.");
            }

            var key = line[..separatorIndex].Trim().Replace("__", ":", StringComparison.Ordinal);
            values[key] = ParseValue(line[(separatorIndex + 1)..].Trim());
        }

        return values;
    }

    private static string ParseValue(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        // An unquoted value ends at an inline comment that follows whitespace.
        var commentIndex = value.IndexOf(" #", StringComparison.Ordinal);
        return commentIndex < 0 ? value : value[..commentIndex].TrimEnd();
    }

    private static string? FindDotEnvFile(string startPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        var candidates = new List<string>();
        while (directory is not null)
        {
            candidates.Add(Path.Combine(directory.FullName, ".env"));
            if (File.Exists(Path.Combine(directory.FullName, RepositoryRootMarker)))
            {
                return candidates.FirstOrDefault(File.Exists);
            }

            directory = directory.Parent;
        }

        // Outside a repository checkout only the start directory itself is considered.
        return File.Exists(candidates[0]) ? candidates[0] : null;
    }
}
