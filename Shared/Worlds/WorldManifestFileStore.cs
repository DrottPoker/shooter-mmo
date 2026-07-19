using System.Text.Json;
using System.Text.Json.Serialization;
using ShooterMmo.WorldData.Worlds;

namespace ShooterMmo.Shared.Worlds;

public static class WorldManifestFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlyCollection<WorldManifestDocument> LoadAll(string configuredRootPath)
    {
        var rootPath = ResolveRootPath(configuredRootPath);
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException(
                $"World data root does not exist at '{rootPath}'.");
        }

        var manifests = Directory
            .EnumerateDirectories(rootPath)
            .Select(directory => LoadFromDirectory(rootPath, directory))
            .OrderBy(manifest => manifest.worldId, StringComparer.Ordinal)
            .ToArray();
        if (manifests.Length == 0)
        {
            throw new InvalidDataException("World data root contains no World manifests.");
        }

        var duplicateWorldId = manifests
            .GroupBy(manifest => manifest.worldId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateWorldId is not null)
        {
            throw new InvalidDataException(
                $"World data root contains duplicate WorldId '{duplicateWorldId}'.");
        }

        return manifests;
    }

    public static WorldManifestDocument Load(string configuredRootPath, string worldId)
    {
        if (!IsValidIdentifier(worldId))
        {
            throw new InvalidDataException("Configured WorldId is invalid.");
        }

        var rootPath = ResolveRootPath(configuredRootPath);
        var worldDirectory = ResolveWorldDirectoryFromRoot(rootPath, worldId);
        return LoadFromDirectory(rootPath, worldDirectory);
    }

    public static string ResolveWorldDirectory(string configuredRootPath, string worldId)
    {
        if (!IsValidIdentifier(worldId))
        {
            throw new InvalidDataException("Configured WorldId is invalid.");
        }

        return ResolveWorldDirectoryFromRoot(ResolveRootPath(configuredRootPath), worldId);
    }

    public static string ResolveActorRuntimePath(string configuredRootPath, string worldId)
    {
        return Path.Combine(
            ResolveWorldDirectory(configuredRootPath, worldId),
            "Runtime",
            "world-actors.json");
    }

    public static string ResolveCollisionRuntimePath(string configuredRootPath, string worldId)
    {
        return Path.Combine(
            ResolveWorldDirectory(configuredRootPath, worldId),
            "Runtime",
            "Resources",
            "ShooterMmo",
            "WorldCollision",
            worldId);
    }

    private static WorldManifestDocument LoadFromDirectory(
        string rootPath,
        string worldDirectory)
    {
        var manifestPath = Path.Combine(worldDirectory, WorldManifestFormat.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException(
                $"World directory '{worldDirectory}' is missing {WorldManifestFormat.FileName}.");
        }

        WorldManifestDocument manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<WorldManifestDocument>(
                File.ReadAllText(manifestPath),
                SerializerOptions)
                ?? throw new InvalidDataException("World manifest has no root object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"World manifest '{manifestPath}' is malformed: {exception.Message}",
                exception);
        }

        try
        {
            WorldManifestValidator.Validate(manifest);
        }
        catch (WorldManifestValidationException exception)
        {
            throw new InvalidDataException(
                $"World manifest '{manifestPath}' is invalid: {exception.Message}",
                exception);
        }

        var directoryName = Path.GetFileName(worldDirectory);
        if (!string.Equals(directoryName, manifest.worldId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World directory '{directoryName}' does not match manifest WorldId '{manifest.worldId}'.");
        }

        var resolvedDirectory = Path.GetFullPath(worldDirectory);
        if (!resolvedDirectory.StartsWith(
                Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("World manifest resolves outside the World data root.");
        }

        return manifest;
    }

    private static string ResolveRootPath(string configuredRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredRootPath))
        {
            throw new InvalidDataException("World data root path is required.");
        }

        var path = Path.IsPathRooted(configuredRootPath)
            ? configuredRootPath
            : Path.Combine(AppContext.BaseDirectory, configuredRootPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string ResolveWorldDirectoryFromRoot(string rootPath, string worldId)
    {
        var worldDirectory = Path.GetFullPath(Path.Combine(rootPath, worldId));
        if (!worldDirectory.StartsWith(
                rootPath + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("WorldId resolves outside the World data root.");
        }

        return worldDirectory;
    }

    private static bool IsValidIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}
