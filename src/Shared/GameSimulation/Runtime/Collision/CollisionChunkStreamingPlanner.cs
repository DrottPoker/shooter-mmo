using System;
using System.Collections.Generic;

namespace Mmorpg.GameSimulation
{
    public readonly struct CollisionChunkCoordinate : IEquatable<CollisionChunkCoordinate>
    {
        public CollisionChunkCoordinate(int x, int z)
        {
            X = x;
            Z = z;
        }

        public int X { get; }

        public int Z { get; }

        public bool Equals(CollisionChunkCoordinate other)
        {
            return X == other.X && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is CollisionChunkCoordinate other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 397) ^ Z;
            }
        }
    }

    public sealed class CollisionChunkStreamingPlanner
    {
        private readonly float chunkSize;
        private readonly int loadRadiusChunks;
        private readonly int unloadRadiusChunks;

        public CollisionChunkStreamingPlanner(
            float chunkSize,
            int loadRadiusChunks,
            int unloadRadiusChunks)
        {
            if (float.IsNaN(chunkSize) || float.IsInfinity(chunkSize) || chunkSize <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }

            if (loadRadiusChunks < 0 || unloadRadiusChunks < loadRadiusChunks)
            {
                throw new ArgumentOutOfRangeException(nameof(loadRadiusChunks));
            }

            this.chunkSize = chunkSize;
            this.loadRadiusChunks = loadRadiusChunks;
            this.unloadRadiusChunks = unloadRadiusChunks;
        }

        public HashSet<CollisionChunkCoordinate> GetRequiredChunks(
            IEnumerable<SimulationVector3> anchors)
        {
            return BuildCoordinates(anchors, loadRadiusChunks);
        }

        public HashSet<CollisionChunkCoordinate> GetRetainedChunks(
            IEnumerable<SimulationVector3> anchors)
        {
            return BuildCoordinates(anchors, unloadRadiusChunks);
        }

        // The chunk an anchor stands in.
        public CollisionChunkCoordinate ToChunk(SimulationVector3 anchor)
        {
            if (!SimulationVector3.IsFinite(anchor))
            {
                throw new ArgumentException("Collision streaming anchors must be finite.", nameof(anchor));
            }

            return new CollisionChunkCoordinate(
                ChunkedStaticCollisionWorld.ToChunkCoordinate(anchor.X, chunkSize),
                ChunkedStaticCollisionWorld.ToChunkCoordinate(anchor.Z, chunkSize));
        }

        // Fills the reusable sets with the chunks around the anchors' chunks, so a caller that
        // keeps its sets allocates nothing once they have grown.
        public void FillChunks(
            IReadOnlyList<CollisionChunkCoordinate> anchorChunks,
            HashSet<CollisionChunkCoordinate> required,
            HashSet<CollisionChunkCoordinate> retained)
        {
            if (anchorChunks == null)
            {
                throw new ArgumentNullException(nameof(anchorChunks));
            }

            if (required == null)
            {
                throw new ArgumentNullException(nameof(required));
            }

            if (retained == null)
            {
                throw new ArgumentNullException(nameof(retained));
            }

            required.Clear();
            retained.Clear();
            for (var index = 0; index < anchorChunks.Count; index++)
            {
                AddAround(anchorChunks[index], loadRadiusChunks, required);
                AddAround(anchorChunks[index], unloadRadiusChunks, retained);
            }
        }

        private HashSet<CollisionChunkCoordinate> BuildCoordinates(
            IEnumerable<SimulationVector3> anchors,
            int radius)
        {
            if (anchors == null)
            {
                throw new ArgumentNullException(nameof(anchors));
            }

            var coordinates = new HashSet<CollisionChunkCoordinate>();
            foreach (var anchor in anchors)
            {
                if (!SimulationVector3.IsFinite(anchor))
                {
                    throw new ArgumentException("Collision streaming anchors must be finite.", nameof(anchors));
                }

                AddAround(ToChunk(anchor), radius, coordinates);
            }

            return coordinates;
        }

        private static void AddAround(
            CollisionChunkCoordinate center,
            int radius,
            HashSet<CollisionChunkCoordinate> coordinates)
        {
            for (var z = center.Z - radius; z <= center.Z + radius; z++)
            {
                for (var x = center.X - radius; x <= center.X + radius; x++)
                {
                    coordinates.Add(new CollisionChunkCoordinate(x, z));
                }
            }
        }
    }
}
