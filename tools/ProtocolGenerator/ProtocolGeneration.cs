using System.Security.Cryptography;
using System.Text;

namespace Mmorpg.Tools.ProtocolGenerator;

public sealed record GeneratedProtocolFile(string RelativePath, string Content);

public sealed record ProtocolGenerationResult(ProtocolModel Model, IReadOnlyList<GeneratedProtocolFile> Files);

// Loads the schema, checks its version against the lock, generates the C# files and their
// Unity .meta files, and writes them, or in verify mode proves that the committed files match.
public static class ProtocolGeneration
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static ProtocolGenerationResult Run(ProtocolGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = Generate(options.RepositoryRoot);
        var directory = ProtocolLayout.Resolve(options.RepositoryRoot, ProtocolLayout.GeneratedDirectory);
        var expected = result.Files.ToDictionary(
            file => ProtocolLayout.Resolve(options.RepositoryRoot, file.RelativePath),
            file => file.Content,
            StringComparer.OrdinalIgnoreCase);
        if (options.VerifyOnly)
        {
            Verify(expected, directory);
        }
        else
        {
            Write(expected, directory);
        }

        return result;
    }

    // Generates every file in memory without touching the disk.
    public static ProtocolGenerationResult Generate(string repositoryRoot)
    {
        var schemaPath = ProtocolLayout.Resolve(repositoryRoot, ProtocolLayout.SchemaPath);
        if (!File.Exists(schemaPath))
        {
            throw new InvalidDataException($"The protocol schema is missing at '{schemaPath}'.");
        }

        var model = ProtocolSchemaLoader.Load(File.ReadAllText(schemaPath));
        var lockPath = ProtocolLayout.Resolve(repositoryRoot, ProtocolLayout.LockPath);
        var lockContent = ProtocolVersionLock.Update(
            File.Exists(lockPath) ? NormalizeLineEndings(File.ReadAllText(lockPath)) : null,
            model.Version,
            ProtocolFingerprint.Compute(model));
        var files = new List<GeneratedProtocolFile>
        {
            new(ProtocolLayout.LockPath, lockContent),
            new(ProtocolLayout.GeneratedDirectory + ".meta", FolderMeta(ProtocolLayout.GeneratedDirectory))
        };
        foreach (var (fileName, content) in ProtocolEmitter.Emit(model))
        {
            var relativePath = ProtocolLayout.GeneratedDirectory + "/" + fileName;
            files.Add(new GeneratedProtocolFile(relativePath, content));
            files.Add(new GeneratedProtocolFile(relativePath + ".meta", ScriptMeta(relativePath)));
        }

        return new ProtocolGenerationResult(model, files);
    }

    // Unity identifies assets by the GUID in their .meta file. Deriving it from the package
    // path keeps it stable on every machine without running Unity.
    public static string AssetGuid(string relativePath)
    {
        var packagePath = ProtocolLayout.PackageName + relativePath[ProtocolLayout.PackageDirectory.Length..];
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(packagePath)));
    }

    // The exact format Unity 6000.5 writes for a new script.
    private static string ScriptMeta(string relativePath)
    {
        return $"fileFormatVersion: 2\nguid: {AssetGuid(relativePath)}";
    }

    // The exact format Unity writes for a folder, including its trailing spaces.
    private static string FolderMeta(string relativePath)
    {
        return "fileFormatVersion: 2\n"
            + $"guid: {AssetGuid(relativePath)}\n"
            + "folderAsset: yes\n"
            + "DefaultImporter:\n"
            + "  externalObjects: {}\n"
            + "  userData: \n"
            + "  assetBundleName: \n"
            + "  assetBundleVariant: \n";
    }

    private static void Verify(IReadOnlyDictionary<string, string> expected, string directory)
    {
        foreach (var (path, content) in expected)
        {
            if (!File.Exists(path) || NormalizeLineEndings(File.ReadAllText(path)) != content)
            {
                throw new InvalidDataException(
                    $"Generated protocol file '{path}' is stale. Run tools/ProtocolGenerator without --verify.");
            }
        }

        foreach (var path in ExistingFiles(directory))
        {
            if (!expected.ContainsKey(path))
            {
                throw new InvalidDataException(
                    $"'{path}' is not generated from the schema. Run tools/ProtocolGenerator without --verify.");
            }
        }
    }

    private static void Write(IReadOnlyDictionary<string, string> expected, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var path in ExistingFiles(directory).Where(path => !expected.ContainsKey(path)))
        {
            File.Delete(path);
        }

        foreach (var (path, content) in expected)
        {
            if (!File.Exists(path) || NormalizeLineEndings(File.ReadAllText(path)) != content)
            {
                File.WriteAllText(path, content, Utf8WithoutBom);
            }
        }
    }

    private static IEnumerable<string> ExistingFiles(string directory)
    {
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(Path.GetFullPath)
            : [];
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
