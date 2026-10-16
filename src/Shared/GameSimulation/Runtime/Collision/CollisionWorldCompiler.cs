using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Mmorpg.GameSimulation
{
    [Serializable]
    public sealed class CollisionWorldAuthoringDocument
    {
        public int FormatVersion;
        public string WorldId;
        public float ChunkSize;
        public CollisionBoxAuthoringEntry[] Boxes;
        public CollisionMeshAuthoringEntry[] Meshes;
        public CollisionHeightfieldAuthoringEntry[] Heightfields;
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
            var stableId = CollisionWorldCompiler.ResolveStableId(StableId, SourcePath);

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
            return CollisionWorldCompiler.CanonicalizeZero(value);
        }
    }

    // A triangle mesh in world space, baked from a non-convex MeshCollider.
    [Serializable]
    public sealed class CollisionMeshAuthoringEntry
    {
        public string StableId;
        public string SourcePath;
        public uint LayerMask;

        // World positions as little-endian float32 x, y, z values, base64 encoded.
        public string Vertices;

        // Vertex index triples as little-endian int32 values, base64 encoded. Each
        // triangle faces the side from which its vertices run clockwise.
        public string Triangles;

        // Degenerate triangles have no front and are skipped.
        public IEnumerable<CollisionTriangle> ToCollisionTriangles()
        {
            var stableId = CollisionWorldCompiler.ResolveStableId(StableId, SourcePath);
            var vertices = CollisionAuthoringEncoding.DecodeFloats(Vertices);
            var indices = CollisionAuthoringEncoding.DecodeInts(Triangles);
            if (vertices.Length == 0 || vertices.Length % 3 != 0)
            {
                throw new InvalidDataException(
                    "Collision mesh '" + SourcePath + "' must contain whole vertex positions.");
            }

            if (indices.Length == 0 || indices.Length % 3 != 0)
            {
                throw new InvalidDataException(
                    "Collision mesh '" + SourcePath + "' must contain whole triangles.");
            }

            var vertexCount = vertices.Length / 3;
            var triangles = new List<CollisionTriangle>(indices.Length / 3);
            for (var triangle = 0; triangle < indices.Length / 3; triangle++)
            {
                var a = ReadVertex(vertices, indices[triangle * 3], vertexCount);
                var b = ReadVertex(vertices, indices[(triangle * 3) + 1], vertexCount);
                var c = ReadVertex(vertices, indices[(triangle * 3) + 2], vertexCount);
                if (!CollisionTriangle.TryCalculateNormal(a, b, c, out _))
                {
                    continue;
                }

                triangles.Add(new CollisionTriangle(
                    CollisionWorldCompiler.TriangleStableId(stableId, triangle),
                    LayerMask,
                    a,
                    b,
                    c));
            }

            return triangles;
        }

        private SimulationVector3 ReadVertex(float[] vertices, int index, int vertexCount)
        {
            if (index < 0 || index >= vertexCount)
            {
                throw new InvalidDataException(
                    "Collision mesh '" + SourcePath + "' references a missing vertex.");
            }

            return new SimulationVector3(
                CollisionWorldCompiler.CanonicalizeZero(vertices[index * 3]),
                CollisionWorldCompiler.CanonicalizeZero(vertices[(index * 3) + 1]),
                CollisionWorldCompiler.CanonicalizeZero(vertices[(index * 3) + 2]));
        }
    }

    // A regular grid of world heights, baked from a TerrainCollider. Each cell becomes two
    // triangles that face up, split along the diagonal from its lowest to its highest
    // sample coordinates.
    [Serializable]
    public sealed class CollisionHeightfieldAuthoringEntry
    {
        public string StableId;
        public string SourcePath;
        public uint LayerMask;
        public float OriginX;
        public float OriginZ;
        public float SpacingX;
        public float SpacingZ;
        public int SamplesX;
        public int SamplesZ;

        // World heights as little-endian float32 values, base64 encoded. Each row holds
        // SamplesX samples along +X, and rows follow along +Z.
        public string Heights;

        // One byte per cell in the same row order, base64 encoded, where 1 removes the
        // cell. Empty when the heightfield has no holes.
        public string Holes;

        public IEnumerable<CollisionTriangle> ToCollisionTriangles()
        {
            var stableId = CollisionWorldCompiler.ResolveStableId(StableId, SourcePath);
            if (SamplesX < 2
                || SamplesZ < 2
                || (long)SamplesX * SamplesZ > CollisionDataFormat.MaximumShapesPerChunk
                || !IsPositiveFinite(SpacingX)
                || !IsPositiveFinite(SpacingZ)
                || !IsFinite(OriginX)
                || !IsFinite(OriginZ))
            {
                throw new InvalidDataException(
                    "Collision heightfield '" + SourcePath + "' has an invalid grid.");
            }

            var heights = CollisionAuthoringEncoding.DecodeFloats(Heights);
            if (heights.Length != SamplesX * SamplesZ || heights.Any(height => !IsFinite(height)))
            {
                throw new InvalidDataException(
                    "Collision heightfield '" + SourcePath + "' must contain one finite height per sample.");
            }

            var cellsX = SamplesX - 1;
            var cellsZ = SamplesZ - 1;
            var holes = CollisionAuthoringEncoding.DecodeBytes(Holes);
            if (holes.Length != 0 && holes.Length != cellsX * cellsZ)
            {
                throw new InvalidDataException(
                    "Collision heightfield '" + SourcePath + "' must contain one hole flag per cell.");
            }

            var triangles = new List<CollisionTriangle>(cellsX * cellsZ * 2);
            for (var cellZ = 0; cellZ < cellsZ; cellZ++)
            {
                for (var cellX = 0; cellX < cellsX; cellX++)
                {
                    var cell = (cellZ * cellsX) + cellX;
                    if (holes.Length != 0 && holes[cell] != 0)
                    {
                        continue;
                    }

                    var lowLow = Sample(heights, cellX, cellZ);
                    var highLow = Sample(heights, cellX + 1, cellZ);
                    var lowHigh = Sample(heights, cellX, cellZ + 1);
                    var highHigh = Sample(heights, cellX + 1, cellZ + 1);
                    triangles.Add(new CollisionTriangle(
                        CollisionWorldCompiler.TriangleStableId(stableId, cell * 2),
                        LayerMask,
                        lowLow,
                        lowHigh,
                        highHigh));
                    triangles.Add(new CollisionTriangle(
                        CollisionWorldCompiler.TriangleStableId(stableId, (cell * 2) + 1),
                        LayerMask,
                        lowLow,
                        highHigh,
                        highLow));
                }
            }

            return triangles;
        }

        private SimulationVector3 Sample(float[] heights, int sampleX, int sampleZ)
        {
            return new SimulationVector3(
                CollisionWorldCompiler.CanonicalizeZero((float)(OriginX + (float)(sampleX * SpacingX))),
                CollisionWorldCompiler.CanonicalizeZero(heights[(sampleZ * SamplesX) + sampleX]),
                CollisionWorldCompiler.CanonicalizeZero((float)(OriginZ + (float)(sampleZ * SpacingZ))));
        }

        private static bool IsPositiveFinite(float value)
        {
            return IsFinite(value) && value > 0f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    // Packs large numeric arrays into compact, exact base64 strings for authoring JSON.
    public static class CollisionAuthoringEncoding
    {
        public static string EncodeFloats(IReadOnlyList<float> values)
        {
            var bytes = new byte[values.Count * 4];
            for (var index = 0; index < values.Count; index++)
            {
                WriteInt32(bytes, index * 4, BitConverter.SingleToInt32Bits(values[index]));
            }

            return Convert.ToBase64String(bytes);
        }

        public static string EncodeInts(IReadOnlyList<int> values)
        {
            var bytes = new byte[values.Count * 4];
            for (var index = 0; index < values.Count; index++)
            {
                WriteInt32(bytes, index * 4, values[index]);
            }

            return Convert.ToBase64String(bytes);
        }

        public static string EncodeBytes(IReadOnlyList<byte> values)
        {
            return values.Count == 0 ? string.Empty : Convert.ToBase64String(values.ToArray());
        }

        public static float[] DecodeFloats(string value)
        {
            var ints = DecodeInts(value);
            var floats = new float[ints.Length];
            for (var index = 0; index < ints.Length; index++)
            {
                floats[index] = BitConverter.Int32BitsToSingle(ints[index]);
            }

            return floats;
        }

        public static int[] DecodeInts(string value)
        {
            var bytes = DecodeBytes(value);
            if (bytes.Length % 4 != 0)
            {
                throw new InvalidDataException("Collision authoring array is not whole 32-bit values.");
            }

            var values = new int[bytes.Length / 4];
            for (var index = 0; index < values.Length; index++)
            {
                var offset = index * 4;
                values[index] = bytes[offset]
                    | (bytes[offset + 1] << 8)
                    | (bytes[offset + 2] << 16)
                    | (bytes[offset + 3] << 24);
            }

            return values;
        }

        public static byte[] DecodeBytes(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Array.Empty<byte>();
            }

            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Collision authoring array is not valid base64.", exception);
            }
        }

        private static void WriteInt32(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
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

            if (!CollisionDataFormat.IsValidChunkSize(authoring.ChunkSize))
            {
                throw new InvalidDataException("Collision authoring chunk size is invalid.");
            }

            var shapes = new List<CollisionShape>();
            foreach (var box in authoring.Boxes ?? Array.Empty<CollisionBoxAuthoringEntry>())
            {
                shapes.Add(box.ToCollisionBox());
            }

            foreach (var mesh in authoring.Meshes ?? Array.Empty<CollisionMeshAuthoringEntry>())
            {
                shapes.AddRange(mesh.ToCollisionTriangles());
            }

            foreach (var heightfield in authoring.Heightfields
                ?? Array.Empty<CollisionHeightfieldAuthoringEntry>())
            {
                shapes.AddRange(heightfield.ToCollisionTriangles());
            }

            if (shapes.Count == 0)
            {
                throw new InvalidDataException("Collision authoring must contain at least one shape.");
            }

            if (shapes.Select(shape => shape.StableId).Distinct().Count() != shapes.Count)
            {
                throw new InvalidDataException("Collision authoring contains duplicate stable ids.");
            }

            var shapesByChunk = new Dictionary<long, List<CollisionShape>>();
            foreach (var shape in shapes)
            {
                var minimumX = ChunkedStaticCollisionWorld.ToChunkCoordinate(
                    shape.Bounds.Minimum.X,
                    authoring.ChunkSize);
                // A shape that ends on a chunk edge stays out of the next chunk, and a
                // flat shape lying on a chunk edge, such as a wall triangle, still gets
                // the chunk that starts there.
                var maximumX = Math.Max(
                    minimumX,
                    ChunkedStaticCollisionWorld.ToChunkCoordinate(
                        (float)(shape.Bounds.Maximum.X - ChunkMaximumEpsilon),
                        authoring.ChunkSize));
                var minimumZ = ChunkedStaticCollisionWorld.ToChunkCoordinate(
                    shape.Bounds.Minimum.Z,
                    authoring.ChunkSize);
                var maximumZ = Math.Max(
                    minimumZ,
                    ChunkedStaticCollisionWorld.ToChunkCoordinate(
                        (float)(shape.Bounds.Maximum.Z - ChunkMaximumEpsilon),
                        authoring.ChunkSize));
                if (ChunkedStaticCollisionWorld.CellCount(minimumX, maximumX, minimumZ, maximumZ)
                    > CollisionDataFormat.MaximumChunksPerShape)
                {
                    throw new InvalidDataException(
                        "Collision shape '" + shape.StableId + "' spans too many chunks.");
                }

                for (var z = minimumZ; z <= maximumZ; z++)
                {
                    for (var x = minimumX; x <= maximumX; x++)
                    {
                        var key = ChunkedStaticCollisionWorld.ChunkKey(x, z);
                        if (!shapesByChunk.TryGetValue(key, out var chunkShapes))
                        {
                            chunkShapes = new List<CollisionShape>();
                            shapesByChunk.Add(key, chunkShapes);
                        }

                        chunkShapes.Add(shape);
                    }
                }
            }

            var compiledChunks = new List<CompiledCollisionChunk>();
            foreach (var pair in shapesByChunk.OrderBy(value => value.Key))
            {
                DecodeChunkKey(pair.Key, out var x, out var z);
                var minimumX = (float)(x * authoring.ChunkSize);
                var minimumZ = (float)(z * authoring.ChunkSize);
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

        public static ulong ResolveStableId(string stableId, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(stableId))
            {
                return StableIdFromPath(sourcePath);
            }

            if (!ulong.TryParse(
                stableId,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var parsed)
                || parsed == 0)
            {
                throw new InvalidDataException(
                    "Collision authoring stable id is not a hexadecimal unsigned integer.");
            }

            return parsed;
        }

        // A triangle id hashes its source entry id with its index in that entry.
        public static ulong TriangleStableId(ulong sourceStableId, int triangleIndex)
        {
            const ulong prime = 1099511628211UL;
            var result = 14695981039346656037UL;
            for (var shift = 0; shift < 64; shift += 8)
            {
                result ^= (sourceStableId >> shift) & 0xffUL;
                result *= prime;
            }

            for (var shift = 0; shift < 32; shift += 8)
            {
                result ^= ((ulong)(uint)triangleIndex >> shift) & 0xffUL;
                result *= prime;
            }

            return result == 0 ? 1UL : result;
        }

        // Unity and .NET can disagree on the sign of zero, so both bake positive zero.
        public static float CanonicalizeZero(float value)
        {
            return value == 0f ? 0f : value;
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

        // The revision is derived from the manifest content, so a loader can detect a
        // manifest whose revision was edited or left stale after a partial bake.
        public static string ComputeManifestRevision(CollisionWorldManifest manifest)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            if (manifest.Chunks == null || string.IsNullOrWhiteSpace(manifest.WorldId))
            {
                throw new InvalidDataException("Collision manifest content is incomplete.");
            }

            return ComputeRevision(manifest.WorldId.Trim(), manifest.ChunkSize, manifest.Chunks);
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
