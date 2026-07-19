using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShooterMmo.GameSimulation
{
    [Serializable]
    public sealed class CollisionWorldAuthoringDocument
    {
        public int FormatVersion;
        public string WorldId;
        public float ChunkSize;
        public CollisionBoxAuthoringEntry[] Boxes;
    }

    [Serializable]
    public sealed class CollisionBoxAuthoringEntry
    {
        public string StableId;
        public string SourcePath;
        public uint LayerMask;
        public float CenterX;
        public float CenterY;
        public float CenterZ;
        public float HalfExtentX;
        public float HalfExtentY;
        public float HalfExtentZ;
        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;

        public CollisionBox ToCollisionBox()
        {
            var stableId = string.IsNullOrWhiteSpace(StableId)
                ? CollisionWorldCompiler.StableIdFromPath(SourcePath)
                : ParseStableId(StableId);

            return new CollisionBox(
                stableId,
                LayerMask,
                new SimulationVector3(
                    CanonicalizeZero(CenterX),
                    CanonicalizeZero(CenterY),
                    CanonicalizeZero(CenterZ)),
                new SimulationVector3(
                    CanonicalizeZero(HalfExtentX),
                    CanonicalizeZero(HalfExtentY),
                    CanonicalizeZero(HalfExtentZ)),
                new SimulationQuaternion(
                    CanonicalizeZero(RotationX),
                    CanonicalizeZero(RotationY),
                    CanonicalizeZero(RotationZ),
                    CanonicalizeZero(RotationW)));
        }

        private static float CanonicalizeZero(float value)
        {
            return value == 0f ? 0f : value;
        }

        private static ulong ParseStableId(string value)
        {
            if (!ulong.TryParse(
                value,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var stableId)
                || stableId == 0)
            {
                throw new InvalidDataException(
                    "Collision authoring stable id is not a hexadecimal unsigned integer.");
            }

            return stableId;
        }
    }

    public sealed class CompiledCollisionChunk
    {
        public CompiledCollisionChunk(CollisionChunkManifestEntry manifestEntry, byte[] data)
        {
            ManifestEntry = manifestEntry;
            Data = data;
        }

        public CollisionChunkManifestEntry ManifestEntry { get; }

        public byte[] Data { get; }
    }

    public sealed class CollisionWorldBakeResult
    {
        public CollisionWorldBakeResult(
            CollisionWorldManifest manifest,
            IReadOnlyList<CompiledCollisionChunk> chunks)
        {
            Manifest = manifest;
            Chunks = chunks;
        }

        public CollisionWorldManifest Manifest { get; }

        public IReadOnlyList<CompiledCollisionChunk> Chunks { get; }
    }

    public static class CollisionWorldCompiler
    {
        private const float ChunkMaximumEpsilon = 0.0001f;

        public static CollisionWorldBakeResult Compile(CollisionWorldAuthoringDocument authoring)
        {
            if (authoring == null)
            {
                throw new ArgumentNullException(nameof(authoring));
            }

            if (authoring.FormatVersion != CollisionDataFormat.Version)
            {
                throw new InvalidDataException("Collision authoring format version is unsupported.");
            }

            if (string.IsNullOrWhiteSpace(authoring.WorldId))
            {
                throw new InvalidDataException("Collision authoring world id is required.");
            }

            if (float.IsNaN(authoring.ChunkSize)
                || float.IsInfinity(authoring.ChunkSize)
                || authoring.ChunkSize <= 0f)
            {
                throw new InvalidDataException("Collision authoring chunk size is invalid.");
            }

            if (authoring.Boxes == null || authoring.Boxes.Length == 0)
            {
                throw new InvalidDataException("Collision authoring must contain at least one box.");
            }

            var boxes = authoring.Boxes.Select(entry => entry.ToCollisionBox()).ToArray();
            if (boxes.Select(box => box.StableId).Distinct().Count() != boxes.Length)
            {
                throw new InvalidDataException("Collision authoring contains duplicate stable ids.");
            }

            var boxesByChunk = new Dictionary<long, List<CollisionBox>>();
            foreach (var box in boxes)
            {
                var minimumX = ChunkedStaticCollisionWorld.ToChunkCoordinate(
                    box.Bounds.Minimum.X,
                    authoring.ChunkSize);
                var maximumX = ChunkedStaticCollisionWorld.ToChunkCoordinate(
                    box.Bounds.Maximum.X - ChunkMaximumEpsilon,
                    authoring.ChunkSize);
                var minimumZ = ChunkedStaticCollisionWorld.ToChunkCoordinate(
                    box.Bounds.Minimum.Z,
                    authoring.ChunkSize);
                var maximumZ = ChunkedStaticCollisionWorld.ToChunkCoordinate(
                    box.Bounds.Maximum.Z - ChunkMaximumEpsilon,
                    authoring.ChunkSize);

                for (var z = minimumZ; z <= maximumZ; z++)
                {
                    for (var x = minimumX; x <= maximumX; x++)
                    {
                        var key = ChunkedStaticCollisionWorld.ChunkKey(x, z);
                        if (!boxesByChunk.TryGetValue(key, out var chunkBoxes))
                        {
                            chunkBoxes = new List<CollisionBox>();
                            boxesByChunk.Add(key, chunkBoxes);
                        }

                        chunkBoxes.Add(box);
                    }
                }
            }

            var compiledChunks = new List<CompiledCollisionChunk>();
            foreach (var pair in boxesByChunk.OrderBy(value => value.Key))
            {
                DecodeChunkKey(pair.Key, out var x, out var z);
                var minimumX = x * authoring.ChunkSize;
                var minimumZ = z * authoring.ChunkSize;
                var bounds = new CollisionAabb(
                    new SimulationVector3(minimumX, float.MinValue, minimumZ),
                    new SimulationVector3(
                        minimumX + authoring.ChunkSize,
                        float.MaxValue,
                        minimumZ + authoring.ChunkSize));
                var chunk = new CollisionChunk(x, z, bounds, pair.Value);
                var data = CollisionChunkCodec.Encode(chunk);
                var resourceName = "chunk_" + CoordinateName(x) + "_" + CoordinateName(z);
                var manifestEntry = new CollisionChunkManifestEntry
                {
                    X = x,
                    Z = z,
                    ResourceName = resourceName,
                    Sha256 = ComputeSha256Hex(data),
                    MinimumX = bounds.Minimum.X,
                    MinimumZ = bounds.Minimum.Z,
                    MaximumX = bounds.Maximum.X,
                    MaximumZ = bounds.Maximum.Z
                };
                compiledChunks.Add(new CompiledCollisionChunk(manifestEntry, data));
            }

            var revision = ComputeRevision(
                authoring.WorldId.Trim(),
                authoring.ChunkSize,
                compiledChunks.Select(chunk => chunk.ManifestEntry));
            var manifest = new CollisionWorldManifest
            {
                FormatVersion = CollisionDataFormat.Version,
                WorldId = authoring.WorldId.Trim(),
                Revision = revision,
                ChunkSize = authoring.ChunkSize,
                Chunks = compiledChunks.Select(chunk => chunk.ManifestEntry).ToArray()
            };
            return new CollisionWorldBakeResult(manifest, compiledChunks);
        }

        public static string ComputeSha256Hex(byte[] data)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(data);
                var builder = new StringBuilder(hash.Length * 2);
                for (var index = 0; index < hash.Length; index++)
                {
                    builder.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        public static ulong StableIdFromPath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Collision source path is required.", nameof(sourcePath));
            }

            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var result = offset;
            var bytes = Encoding.UTF8.GetBytes(sourcePath.Trim());
            for (var index = 0; index < bytes.Length; index++)
            {
                result ^= bytes[index];
                result *= prime;
            }

            return result == 0 ? 1UL : result;
        }

        private static string ComputeRevision(
            string worldId,
            float chunkSize,
            IEnumerable<CollisionChunkManifestEntry> chunks)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(CollisionDataFormat.Version);
                writer.Write(worldId);
                writer.Write(chunkSize);
                foreach (var chunk in chunks.OrderBy(value => value.X).ThenBy(value => value.Z))
                {
                    writer.Write(chunk.X);
                    writer.Write(chunk.Z);
                    writer.Write(chunk.Sha256);
                }

                writer.Flush();
                return ComputeSha256Hex(stream.ToArray());
            }
        }

        private static string CoordinateName(int value)
        {
            return value < 0
                ? "n" + Math.Abs((long)value).ToString(CultureInfo.InvariantCulture)
                : "p" + value.ToString(CultureInfo.InvariantCulture);
        }

        private static void DecodeChunkKey(long key, out int x, out int z)
        {
            x = (int)(key >> 32);
            z = (int)key;
        }
    }
}
