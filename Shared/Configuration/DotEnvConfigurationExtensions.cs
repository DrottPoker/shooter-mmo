using Microsoft.Extensions.Configuration;

namespace ShooterMmo.Shared.Configuration;

public static class DotEnvConfigurationExtensions
{
    public static IConfigurationBuilder AddOptionalDotEnvFile(
        this IConfigurationBuilder configuration,
        string contentRootPath)
    {
        var path = FindInParentDirectories(contentRootPath, ".env");
        if (path is null)
        {
            return configuration;
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                throw new InvalidOperationException($"Invalid .env entry in {path}: {rawLine}");
            }

            var key = line[..separatorIndex].Trim().Replace("__", ":", StringComparison.Ordinal);
            var value = line[(separatorIndex + 1)..].Trim();
            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"')
                    || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        configuration.AddInMemoryCollection(values);
        return configuration;
    }

    private static string? FindInParentDirectories(string startPath, string fileName)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        for (var depth = 0; directory is not null && depth < 4; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
