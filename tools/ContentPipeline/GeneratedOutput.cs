using System.Text;

namespace Mmorpg.Tools.ContentPipeline;

// Writes generated files, or in verify mode proves that the committed files match.
public static class GeneratedOutput
{
    private const string MetaSuffix = ".meta";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void WriteOrVerifyText(
        string path,
        string expectedText,
        bool verifyOnly,
        string label)
    {
        if (verifyOnly)
        {
            if (!File.Exists(path)
                || !string.Equals(
                    StrictContentJson.NormalizeLineEndings(File.ReadAllText(path)),
                    expectedText,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{label} is stale at '{path}'. Run the compiler without --verify.");
            }

            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, expectedText, Utf8WithoutBom);
    }

    // Generated output left behind by content that no longer exists. Verify mode rejects
    // it; compile mode deletes it together with its Unity .meta file.
    public static void RemoveOrRejectStale(
        IEnumerable<string> stalePaths,
        bool verifyOnly,
        string label)
    {
        foreach (var path in stalePaths)
        {
            if (verifyOnly)
            {
                throw new InvalidDataException(
                    $"{label} at '{path}' has no content source. Run the compiler without --verify.");
            }

            DeleteWithMeta(path);
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)
                && Directory.Exists(parent)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                DeleteWithMeta(parent);
            }
        }
    }

    // The entries of a generated directory other than the expected ones and their Unity .meta
    // files, for RemoveOrRejectStale. Hidden files are ignored, and a stale entry's .meta file is
    // left out because it goes with its entry.
    public static string[] UnexpectedEntries(string directory, IReadOnlyCollection<string> expectedNames)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var unexpected = Directory.EnumerateFileSystemEntries(directory)
            .Select(path => Path.GetFileName(path))
            .Where(name => !name.StartsWith('.')
                && !expectedNames.Contains(name, StringComparer.Ordinal)
                && !expectedNames.Contains(MetaOwner(name), StringComparer.Ordinal))
            .ToArray();
        return unexpected
            .Where(name => !unexpected.Contains(MetaOwner(name), StringComparer.Ordinal))
            .Select(name => Path.Combine(directory, name))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    // The entry a Unity .meta file belongs to, or an empty name for any other file.
    private static string MetaOwner(string name)
    {
        return name.EndsWith(MetaSuffix, StringComparison.Ordinal)
            ? name[..^MetaSuffix.Length]
            : string.Empty;
    }

    private static void DeleteWithMeta(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }

        var metaPath = path + MetaSuffix;
        if (File.Exists(metaPath))
        {
            File.Delete(metaPath);
        }
    }
}
