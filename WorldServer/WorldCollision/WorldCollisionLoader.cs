using System.Text.Json;
using ShooterMmo.GameSimulation;
using WorldServer.Config;

namespace WorldServer.WorldCollision;

public static class WorldCollisionLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true
    };

    public static ChunkedStaticCollisionWorld Load(WorldServerConfig config)
    {
        var rootPath = Path.IsPathRooted(config.CollisionDataPath)
            ? config.CollisionDataPath
            : Path.Combine(AppContext.BaseDirectory, config.CollisionDataPath);
        var fullRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var worldPath = Path.GetFullPath(Path.Combine(fullRootPath, config.WorldServerId));
        if (!worldPath.StartsWith(
                fullRootPath + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Configured world id resolves outside the collision data root.");
        }
        var manifestPath = Path.Combine(worldPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Collision manifest does not exist for world '{config.WorldServerId}' at '{manifestPath}'.");
        }

        CollisionWorldManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CollisionWorldManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions) ?? throw new InvalidDataException("Collision manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Collision manifest JSON is invalid.", exception);
        }

        ValidateManifest(manifest, config.WorldServerId);
        var chunks = new List<CollisionChunk>(manifest.Chunks.Length);
        foreach (var entry in manifest.Chunks)
        {
            var chunkPath = Path.Combine(worldPath, entry.ResourceName + ".bytes");
            if (!File.Exists(chunkPath))
            {
                throw new InvalidOperationException(
                    $"Collision chunk '{entry.ResourceName}' does not exist at '{chunkPath}'.");
            }

            var bytes = File.ReadAllBytes(chunkPath);
            var actualHash = CollisionWorldCompiler.ComputeSha256Hex(bytes);
            if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Collision chunk '{entry.ResourceName}' failed checksum validation.");
            }

            CollisionChunk chunk;
            try
            {
                chunk = CollisionChunkCodec.Decode(bytes);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidOperationException(
                    $"Collision chunk '{entry.ResourceName}' is invalid.",
                    exception);
            }

            if (chunk.X != entry.X || chunk.Z != entry.Z)
            {
                throw new InvalidOperationException(
                    $"Collision chunk '{entry.ResourceName}' coordinates do not match its manifest entry.");
            }

            chunks.Add(chunk);
        }

        return new ChunkedStaticCollisionWorld(
            manifest.WorldId,
            manifest.Revision,
            manifest.ChunkSize,
            chunks);
    }

    private static void ValidateManifest(CollisionWorldManifest manifest, string expectedWorldId)
    {
        if (manifest.FormatVersion != CollisionDataFormat.Version)
        {
            throw new InvalidOperationException(
                $"Collision format version {manifest.FormatVersion} is unsupported.");
        }

        if (!string.Equals(manifest.WorldId, expectedWorldId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Collision manifest world '{manifest.WorldId}' does not match configured world '{expectedWorldId}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Revision)
            || float.IsNaN(manifest.ChunkSize)
            || float.IsInfinity(manifest.ChunkSize)
            || manifest.ChunkSize <= 0f
            || manifest.Chunks is null
            || manifest.Chunks.Length == 0)
        {
            throw new InvalidOperationException("Collision manifest metadata is invalid.");
        }

        var coordinates = new HashSet<long>();
        foreach (var entry in manifest.Chunks)
        {
            var key = ((long)entry.X << 32) | (uint)entry.Z;
            if (string.IsNullOrWhiteSpace(entry.ResourceName)
                || entry.ResourceName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || entry.ResourceName.Contains(Path.DirectorySeparatorChar)
                || entry.ResourceName.Contains(Path.AltDirectorySeparatorChar)
                || string.IsNullOrWhiteSpace(entry.Sha256)
                || entry.Sha256.Length != 64
                || !coordinates.Add(key))
            {
                throw new InvalidOperationException("Collision manifest contains an invalid chunk entry.");
            }
        }
    }
}
