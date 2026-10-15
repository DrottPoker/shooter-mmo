using System;
using System.Collections.Generic;

namespace Mmorpg.GameSimulation
{
    public sealed class ChunkedStaticCollisionWorld : ICollisionWorld
    {
        private readonly object syncRoot = new object();
        private readonly Dictionary<long, CollisionChunk> chunks = new Dictionary<long, CollisionChunk>();

        public ChunkedStaticCollisionWorld(
            string worldId,
            string revision,
            float chunkSize)
            : this(worldId, revision, chunkSize, Array.Empty<CollisionChunk>(), true)
        {
        }

        public ChunkedStaticCollisionWorld(
            string worldId,
            string revision,
            float chunkSize,
            IEnumerable<CollisionChunk> collisionChunks)
            : this(worldId, revision, chunkSize, collisionChunks, false)
        {
        }

        private ChunkedStaticCollisionWorld(
            string worldId,
            string revision,
            float chunkSize,
            IEnumerable<CollisionChunk> collisionChunks,
            bool allowEmpty)
        {
            if (string.IsNullOrWhiteSpace(worldId))
            {
                throw new ArgumentException("World id is required.", nameof(worldId));
            }

            if (string.IsNullOrWhiteSpace(revision))
            {
                throw new ArgumentException("Collision revision is required.", nameof(revision));
            }

            if (!CollisionDataFormat.IsValidChunkSize(chunkSize))
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

            if (!allowEmpty && chunks.Count == 0)
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

        public bool IsChunkLoaded(int x, int z)
        {
            lock (syncRoot)
            {
                return chunks.ContainsKey(ChunkKey(x, z));
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
                // A query wider than the loaded set scans the loaded chunks instead, so
                // the cost is bounded by the world and never by the query size.
                if (CellCount(minimumX, maximumX, minimumZ, maximumZ) > chunks.Count)
                {
                    foreach (var chunk in chunks.Values)
                    {
                        if (chunk.X >= minimumX
                            && chunk.X <= maximumX
                            && chunk.Z >= minimumZ
                            && chunk.Z <= maximumZ)
                        {
                            AddIntersectingBoxes(chunk, bounds, layerMask, buffer);
                        }
                    }

                    return;
                }

                for (var z = minimumZ; z <= maximumZ; z++)
                {
                    for (var x = minimumX; x <= maximumX; x++)
                    {
                        if (chunks.TryGetValue(ChunkKey(x, z), out var chunk))
                        {
                            AddIntersectingBoxes(chunk, bounds, layerMask, buffer);
                        }
                    }
                }
            }
        }

        private static void AddIntersectingBoxes(
            CollisionChunk chunk,
            CollisionAabb bounds,
            uint layerMask,
            CollisionQueryBuffer buffer)
        {
            if (!chunk.Bounds.Intersects(bounds))
            {
                return;
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

        private void ValidateChunkBounds(CollisionChunk chunk)
        {
            if (Math.Abs(chunk.X) > CollisionDataFormat.MaximumChunkCoordinate
                || Math.Abs(chunk.Z) > CollisionDataFormat.MaximumChunkCoordinate)
            {
                throw new ArgumentException(
                    "Collision chunk coordinates are outside the supported range.",
                    nameof(chunk));
            }

            const float epsilon = 0.001f;
            var expectedMinimumX = (float)(chunk.X * ChunkSize);
            var expectedMinimumZ = (float)(chunk.Z * ChunkSize);
            var expectedMaximumX = (float)(expectedMinimumX + ChunkSize);
            var expectedMaximumZ = (float)(expectedMinimumZ + ChunkSize);
            if (Math.Abs((float)(chunk.Bounds.Minimum.X - expectedMinimumX)) > epsilon
                || Math.Abs((float)(chunk.Bounds.Minimum.Z - expectedMinimumZ)) > epsilon
                || Math.Abs((float)(chunk.Bounds.Maximum.X - expectedMaximumX)) > epsilon
                || Math.Abs((float)(chunk.Bounds.Maximum.Z - expectedMaximumZ)) > epsilon)
            {
                throw new ArgumentException(
                    "Collision chunk bounds do not match its coordinates.",
                    nameof(chunk));
            }
        }

        // Clamping keeps the conversion identical in every runtime, because a float to
        // int cast outside the int range differs between .NET and Unity.
        internal static int ToChunkCoordinate(float position, float chunkSize)
        {
            var coordinate = Math.Floor((float)(position / chunkSize));
            if (double.IsNaN(coordinate))
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }

            if (coordinate >= CollisionDataFormat.MaximumChunkCoordinate)
            {
                return CollisionDataFormat.MaximumChunkCoordinate;
            }

            if (coordinate <= -CollisionDataFormat.MaximumChunkCoordinate)
            {
                return -CollisionDataFormat.MaximumChunkCoordinate;
            }

            return (int)coordinate;
        }

        internal static long CellCount(int minimumX, int maximumX, int minimumZ, int maximumZ)
        {
            if (maximumX < minimumX || maximumZ < minimumZ)
            {
                return 0;
            }

            return ((long)maximumX - minimumX + 1) * ((long)maximumZ - minimumZ + 1);
        }

        internal static long ChunkKey(int x, int z)
        {
            return ((long)x << 32) | (uint)z;
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
            if (!CollisionDataFormat.IsValidChunkSize(chunkSize))
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

                // A query wider than the occupied cells scans the entries instead, so the
                // cost is bounded by the dynamic boxes and never by the query size.
                if (ChunkedStaticCollisionWorld.CellCount(minimumX, maximumX, minimumZ, maximumZ)
                    > stableIdsByCell.Count)
                {
                    foreach (var entry in entries.Values)
                    {
                        var box = entry.Box;
                        if ((box.LayerMask & layerMask) != 0 && box.Bounds.Intersects(bounds))
                        {
                            buffer.Add(box);
                        }
                    }

                    return;
                }

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
            if (ChunkedStaticCollisionWorld.CellCount(minimumX, maximumX, minimumZ, maximumZ)
                > CollisionDataFormat.MaximumChunksPerBox)
            {
                throw new ArgumentException("Dynamic collision box spans too many cells.");
            }

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
