using System.Text.Json;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Content;

if (!ContentToolOptions.TryParse(args, out var options, out var argumentError))
{
    Console.Error.WriteLine(argumentError);
    Console.Error.WriteLine("Usage: WorldCollisionCompiler " + ContentToolOptions.Usage);
    return 1;
}

var jsonOptions = new JsonSerializerOptions
{
    IncludeFields = true,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
};

try
{
    var worldIds = ContentLayout.ListWorldIds(
        ContentLayout.WorldsDirectory(options!.ContentRoot));
    if (worldIds.Length == 0)
    {
        throw new InvalidDataException(
            $"No World exists below '{ContentLayout.WorldsDirectory(options.ContentRoot)}'.");
    }

    foreach (var worldId in worldIds)
    {
        var result = CompileWorld(options.ContentRoot, worldId, jsonOptions);
        var outputDirectory = ContentLayout.GeneratedCollisionDirectory(
            options.GeneratedRoot,
            worldId);
        if (options.VerifyOnly)
        {
            VerifyOutputs(outputDirectory, result, jsonOptions);
        }
        else
        {
            await WriteOutputs(outputDirectory, result, jsonOptions);
        }

        Console.WriteLine(
            "Collision data {0} for world {1} at revision {2} with {3} chunks.",
            options.VerifyOnly ? "verified" : "compiled",
            result.Manifest.WorldId,
            result.Manifest.Revision,
            result.Chunks.Count);
    }

    GeneratedOutput.RemoveOrRejectStale(
        FindStaleOutputDirectories(options.GeneratedRoot, worldIds),
        options.VerifyOnly,
        "Compiled collision data");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Collision compilation failed: " + exception.Message);
    return 1;
}

static CollisionWorldBakeResult CompileWorld(
    string contentRoot,
    string worldId,
    JsonSerializerOptions jsonOptions)
{
    var authoringPath = ContentLayout.CollisionSourcePath(contentRoot, worldId);
    var authoring = JsonSerializer.Deserialize<CollisionWorldAuthoringDocument>(
        File.ReadAllText(authoringPath),
        jsonOptions) ?? throw new InvalidDataException(
            $"Collision authoring JSON is empty at '{authoringPath}'.");
    var result = CollisionWorldCompiler.Compile(authoring);
    if (!string.Equals(result.Manifest.WorldId, worldId, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Collision authoring '{authoringPath}' must declare worldId '{worldId}'.");
    }

    return result;
}

static async Task WriteOutputs(
    string outputDirectory,
    CollisionWorldBakeResult result,
    JsonSerializerOptions jsonOptions)
{
    Directory.CreateDirectory(outputDirectory);
    foreach (var staleChunk in Directory.EnumerateFiles(outputDirectory, "chunk_*.bytes"))
    {
        File.Delete(staleChunk);
    }

    foreach (var chunk in result.Chunks)
    {
        await File.WriteAllBytesAsync(
            Path.Combine(outputDirectory, chunk.ManifestEntry.ResourceName + ".bytes"),
            chunk.Data);
    }

    await File.WriteAllTextAsync(
        Path.Combine(outputDirectory, ContentLayout.CollisionManifestFileName),
        JsonSerializer.Serialize(result.Manifest, jsonOptions) + Environment.NewLine);
}

static string[] FindStaleOutputDirectories(string generatedRoot, string[] worldIds)
{
    var root = ContentLayout.GeneratedCollisionRoot(generatedRoot);
    if (!Directory.Exists(root))
    {
        return [];
    }

    return Directory.EnumerateDirectories(root)
        .Where(path => !worldIds.Contains(Path.GetFileName(path), StringComparer.Ordinal))
        .Order(StringComparer.Ordinal)
        .ToArray();
}

static void VerifyOutputs(
    string outputDirectory,
    CollisionWorldBakeResult expected,
    JsonSerializerOptions jsonOptions)
{
    var manifestPath = Path.Combine(outputDirectory, ContentLayout.CollisionManifestFileName);
    if (!File.Exists(manifestPath))
    {
        throw new InvalidDataException("Compiled collision manifest is stale.");
    }

    var actualManifest = JsonSerializer.Deserialize<CollisionWorldManifest>(
        File.ReadAllText(manifestPath),
        jsonOptions) ?? throw new InvalidDataException("Compiled collision manifest is empty.");
    if (actualManifest.FormatVersion != expected.Manifest.FormatVersion
        || !string.Equals(actualManifest.WorldId, expected.Manifest.WorldId, StringComparison.Ordinal)
        || !string.Equals(actualManifest.Revision, expected.Manifest.Revision, StringComparison.Ordinal)
        || actualManifest.ChunkSize != expected.Manifest.ChunkSize
        || actualManifest.Chunks is null
        || actualManifest.Chunks.Length != expected.Manifest.Chunks.Length)
    {
        throw new InvalidDataException("Compiled collision manifest is stale.");
    }

    for (var index = 0; index < expected.Manifest.Chunks.Length; index++)
    {
        var actual = actualManifest.Chunks[index];
        var wanted = expected.Manifest.Chunks[index];
        if (actual.X != wanted.X
            || actual.Z != wanted.Z
            || !string.Equals(actual.ResourceName, wanted.ResourceName, StringComparison.Ordinal)
            || !string.Equals(actual.Sha256, wanted.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Compiled collision manifest is stale.");
        }
    }

    var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var chunk in expected.Chunks)
    {
        var fileName = chunk.ManifestEntry.ResourceName + ".bytes";
        expectedFiles.Add(fileName);
        var path = Path.Combine(outputDirectory, fileName);
        if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(chunk.Data))
        {
            throw new InvalidDataException("Compiled collision chunk is stale: " + fileName);
        }
    }

    var unexpected = Directory.Exists(outputDirectory)
        ? Directory.EnumerateFiles(outputDirectory, "chunk_*.bytes")
            .Select(Path.GetFileName)
            .FirstOrDefault(fileName => fileName is not null && !expectedFiles.Contains(fileName))
        : null;
    if (unexpected is not null)
    {
        throw new InvalidDataException("Unexpected compiled collision chunk exists: " + unexpected);
    }
}
