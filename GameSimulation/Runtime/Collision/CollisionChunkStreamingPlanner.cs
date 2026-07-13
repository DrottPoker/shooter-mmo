using System;
using System.Collections.Generic;

namespace ShooterMmo.GameSimulation
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

                var centerX = (int)Math.Floor(anchor.X / chunkSize);
                var centerZ = (int)Math.Floor(anchor.Z / chunkSize);
                for (var z = centerZ - radius; z <= centerZ + radius; z++)
                {
                    for (var x = centerX - radius; x <= centerX + radius; x++)
                    {
                        coordinates.Add(new CollisionChunkCoordinate(x, z));
                    }
                }
            }

            return coordinates;
        }
    }
}
