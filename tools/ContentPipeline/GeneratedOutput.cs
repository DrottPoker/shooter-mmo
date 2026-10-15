using System.Text;

namespace Mmorpg.Tools.ContentPipeline;

// Writes generated files, or in verify mode proves that the committed files match.
public static class GeneratedOutput
{
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

        var metaPath = path + ".meta";
        if (File.Exists(metaPath))
        {
            File.Delete(metaPath);
        }
    }
}
