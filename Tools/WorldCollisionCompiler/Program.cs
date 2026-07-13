using System.Text.Json;
using ShooterMmo.GameSimulation;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine(
        "Usage: WorldCollisionCompiler <authoring.json> <output-directory> [--verify]");
    return 1;
}

var authoringPath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
var verifyOnly = args.Length == 3
    && string.Equals(args[2], "--verify", StringComparison.OrdinalIgnoreCase);

if (args.Length == 3 && !verifyOnly)
{
    Console.Error.WriteLine("The only supported third argument is --verify.");
    return 1;
}

try
{
    if (!File.Exists(authoringPath))
    {
        throw new FileNotFoundException("Collision authoring file does not exist.", authoringPath);
    }

    var jsonOptions = new JsonSerializerOptions
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    var authoring = JsonSerializer.Deserialize<CollisionWorldAuthoringDocument>(
        await File.ReadAllTextAsync(authoringPath),
        jsonOptions) ?? throw new InvalidDataException("Collision authoring JSON is empty.");
    var result = CollisionWorldCompiler.Compile(authoring);
    var manifestJson = JsonSerializer.Serialize(result.Manifest, jsonOptions) + Environment.NewLine;

    if (verifyOnly)
    {
        VerifyOutputs(outputDirectory, result, jsonOptions);
        Console.WriteLine(
            "Collision data verified for world {0} at revision {1} with {2} chunks.",
            result.Manifest.WorldId,
            result.Manifest.Revision,
            result.Chunks.Count);
        return 0;
    }

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

    await File.WriteAllTextAsync(Path.Combine(outputDirectory, "manifest.json"), manifestJson);
    Console.WriteLine(
        "Collision data compiled for world {0} at revision {1} with {2} chunks.",
        result.Manifest.WorldId,
        result.Manifest.Revision,
        result.Chunks.Count);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Collision compilation failed: " + exception.Message);
    return 1;
}

static void VerifyOutputs(
    string outputDirectory,
    CollisionWorldBakeResult expected,
    JsonSerializerOptions jsonOptions)
{
    var manifestPath = Path.Combine(outputDirectory, "manifest.json");
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
