using System;
using System.Collections.Generic;

namespace ShooterMmo.GameSimulation
{
    public sealed class ChunkedStaticCollisionWorld : ICollisionWorld
    {
        private readonly object syncRoot = new object();
        private readonly Dictionary<long, CollisionChunk> chunks = new Dictionary<long, CollisionChunk>();

        public ChunkedStaticCollisionWorld(
            string worldId,
            string revision,
            float chunkSize,
            IEnumerable<CollisionChunk> collisionChunks)
        {
            if (string.IsNullOrWhiteSpace(worldId))
            {
                throw new ArgumentException("World id is required.", nameof(worldId));
            }

            if (string.IsNullOrWhiteSpace(revision))
            {
                throw new ArgumentException("Collision revision is required.", nameof(revision));
            }

            if (!IsFinite(chunkSize) || chunkSize <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }

            WorldId = worldId;
            Revision = revision;
            ChunkSize = chunkSize;

            foreach (var chunk in collisionChunks ?? throw new ArgumentNullException(nameof(collisionChunks)))
            {
                if (!TryLoadChunk(chunk))
                {
                    throw new ArgumentException("Collision chunks contain duplicate coordinates.", nameof(collisionChunks));
                }
            }

            if (chunks.Count == 0)
            {
                throw new ArgumentException("Collision world must contain at least one chunk.", nameof(collisionChunks));
            }
        }

        public string WorldId { get; }

        public string Revision { get; }

        public float ChunkSize { get; }

        public int ChunkCount
        {
            get
            {
                lock (syncRoot)
                {
                    return chunks.Count;
                }
            }
        }

        public bool TryLoadChunk(CollisionChunk chunk)
        {
            if (chunk == null)
            {
                throw new ArgumentNullException(nameof(chunk));
            }

            ValidateChunkBounds(chunk);
            lock (syncRoot)
            {
                var key = ChunkKey(chunk.X, chunk.Z);
                if (chunks.ContainsKey(key))
                {
                    return false;
                }

                chunks.Add(key, chunk);
                return true;
            }
        }

        public bool UnloadChunk(int x, int z)
        {
            lock (syncRoot)
            {
                return chunks.Remove(ChunkKey(x, z));
            }
        }

        public void QueryBoxes(CollisionAabb bounds, uint layerMask, CollisionQueryBuffer buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            var minimumX = ToChunkCoordinate(bounds.Minimum.X, ChunkSize);
            var maximumX = ToChunkCoordinate(bounds.Maximum.X, ChunkSize);
            var minimumZ = ToChunkCoordinate(bounds.Minimum.Z, ChunkSize);
            var maximumZ = ToChunkCoordinate(bounds.Maximum.Z, ChunkSize);

            lock (syncRoot)
            {
                for (var z = minimumZ; z <= maximumZ; z++)
                {
                    for (var x = minimumX; x <= maximumX; x++)
                    {
                        if (!chunks.TryGetValue(ChunkKey(x, z), out var chunk)
                            || !chunk.Bounds.Intersects(bounds))
                        {
                            continue;
                        }

                        for (var index = 0; index < chunk.Boxes.Count; index++)
                        {
                            var box = chunk.Boxes[index];
                            if ((box.LayerMask & layerMask) != 0 && box.Bounds.Intersects(bounds))
                            {
                                buffer.Add(box);
                            }
                        }
                    }
                }
            }
        }

        private void ValidateChunkBounds(CollisionChunk chunk)
        {
            const float epsilon = 0.001f;
            var expectedMinimumX = chunk.X * ChunkSize;
            var expectedMinimumZ = chunk.Z * ChunkSize;
            if (Math.Abs(chunk.Bounds.Minimum.X - expectedMinimumX) > epsilon
                || Math.Abs(chunk.Bounds.Minimum.Z - expectedMinimumZ) > epsilon
                || Math.Abs(chunk.Bounds.Maximum.X - (expectedMinimumX + ChunkSize)) > epsilon
                || Math.Abs(chunk.Bounds.Maximum.Z - (expectedMinimumZ + ChunkSize)) > epsilon)
            {
                throw new ArgumentException(
                    "Collision chunk bounds do not match its coordinates.",
                    nameof(chunk));
            }
        }

        internal static int ToChunkCoordinate(float position, float chunkSize)
        {
            return (int)Math.Floor(position / chunkSize);
        }

        internal static long ChunkKey(int x, int z)
        {
            return ((long)x << 32) | (uint)z;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public sealed class DynamicCollisionWorld : ICollisionWorld
    {
        private readonly object syncRoot = new object();
        private readonly Dictionary<ulong, DynamicCollisionEntry> entries =
            new Dictionary<ulong, DynamicCollisionEntry>();
        private readonly Dictionary<long, HashSet<ulong>> stableIdsByCell =
            new Dictionary<long, HashSet<ulong>>();
        private readonly float chunkSize;

        public DynamicCollisionWorld(float chunkSize)
        {
            if (float.IsNaN(chunkSize) || float.IsInfinity(chunkSize) || chunkSize <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }

            this.chunkSize = chunkSize;
        }

        public int Count
        {
            get
            {
                lock (syncRoot)
                {
                    return entries.Count;
                }
            }
        }

        public void Upsert(CollisionBox box)
        {
            if (box == null)
            {
                throw new ArgumentNullException(nameof(box));
            }

            lock (syncRoot)
            {
                if (entries.TryGetValue(box.StableId, out var previous))
                {
                    RemoveCellMemberships(previous);
                }

                var entry = new DynamicCollisionEntry(box, CalculateCellKeys(box.Bounds));
                entries[box.StableId] = entry;
                AddCellMemberships(entry);
            }
        }

        public bool Remove(ulong stableId)
        {
            lock (syncRoot)
            {
                if (!entries.TryGetValue(stableId, out var entry))
                {
                    return false;
                }

                RemoveCellMemberships(entry);
                return entries.Remove(stableId);
            }
        }

        public void QueryBoxes(CollisionAabb bounds, uint layerMask, CollisionQueryBuffer buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            lock (syncRoot)
            {
                var minimumX = ChunkedStaticCollisionWorld.ToChunkCoordinate(
                    bounds.Minimum.X,
                    chunkSize);
                var maximumX = ChunkedStaticCollisionWorld.ToChunkCoordinate(
                    bounds.Maximum.X,
                    chunkSize);
                var minimumZ = ChunkedStaticCollisionWorld.ToChunkCoordinate(
                    bounds.Minimum.Z,
                    chunkSize);
                var maximumZ = ChunkedStaticCollisionWorld.ToChunkCoordinate(
                    bounds.Maximum.Z,
                    chunkSize);

                for (var z = minimumZ; z <= maximumZ; z++)
                {
                    for (var x = minimumX; x <= maximumX; x++)
                    {
                        if (!stableIdsByCell.TryGetValue(
                                ChunkedStaticCollisionWorld.ChunkKey(x, z),
                                out var stableIds))
                        {
                            continue;
                        }

                        foreach (var stableId in stableIds)
                        {
                            var box = entries[stableId].Box;
                            if ((box.LayerMask & layerMask) != 0 && box.Bounds.Intersects(bounds))
                            {
                                buffer.Add(box);
                            }
                        }
                    }
                }
            }
        }

        private long[] CalculateCellKeys(CollisionAabb bounds)
        {
            var minimumX = ChunkedStaticCollisionWorld.ToChunkCoordinate(bounds.Minimum.X, chunkSize);
            var maximumX = ChunkedStaticCollisionWorld.ToChunkCoordinate(bounds.Maximum.X, chunkSize);
            var minimumZ = ChunkedStaticCollisionWorld.ToChunkCoordinate(bounds.Minimum.Z, chunkSize);
            var maximumZ = ChunkedStaticCollisionWorld.ToChunkCoordinate(bounds.Maximum.Z, chunkSize);
            var keys = new List<long>();
            for (var z = minimumZ; z <= maximumZ; z++)
            {
                for (var x = minimumX; x <= maximumX; x++)
                {
                    keys.Add(ChunkedStaticCollisionWorld.ChunkKey(x, z));
                }
            }

            return keys.ToArray();
        }

        private void AddCellMemberships(DynamicCollisionEntry entry)
        {
            for (var index = 0; index < entry.CellKeys.Length; index++)
            {
                var key = entry.CellKeys[index];
                if (!stableIdsByCell.TryGetValue(key, out var stableIds))
                {
                    stableIds = new HashSet<ulong>();
                    stableIdsByCell.Add(key, stableIds);
                }

                stableIds.Add(entry.Box.StableId);
            }
        }

        private void RemoveCellMemberships(DynamicCollisionEntry entry)
        {
            for (var index = 0; index < entry.CellKeys.Length; index++)
            {
                var key = entry.CellKeys[index];
                if (stableIdsByCell.TryGetValue(key, out var stableIds))
                {
                    stableIds.Remove(entry.Box.StableId);
                    if (stableIds.Count == 0)
                    {
                        stableIdsByCell.Remove(key);
                    }
                }
            }
        }

        private sealed class DynamicCollisionEntry
        {
            public DynamicCollisionEntry(CollisionBox box, long[] cellKeys)
            {
                Box = box;
                CellKeys = cellKeys;
            }

            public CollisionBox Box { get; }

            public long[] CellKeys { get; }
        }
    }

    public sealed class CompositeCollisionWorld : ICollisionWorld
    {
        private readonly ICollisionWorld[] sources;

        public CompositeCollisionWorld(params ICollisionWorld[] sources)
        {
            this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        }

        public void QueryBoxes(CollisionAabb bounds, uint layerMask, CollisionQueryBuffer buffer)
        {
            for (var index = 0; index < sources.Length; index++)
            {
                sources[index].QueryBoxes(bounds, layerMask, buffer);
            }
        }
    }
}
