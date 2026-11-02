using System;
using System.Collections.Generic;

namespace Mmorpg.GameSimulation
{
    // Derives the walls that keep characters out of deep water. The compiler samples the
    // water on a fine grid: the depth at a sample is the height of the water surface above
    // the first ground below it, and a sample whose surface lies under terrain or inside a
    // box is dry land. A marching squares pass traces the line where the depth reaches
    // WaterRules.BlockDepth, and every piece of that line becomes a vertical quad of two
    // one-sided triangles that faces the shallow side. A walker in shallow water meets it
    // like any other wall, and a character that fell into deep water walks out through its
    // back. Only casted float arithmetic, so Unity and .NET bake the same edges.
    internal static class DeepWaterEdges
    {
        public const float SampleSpacing = 0.25f;

        // The walls reach this far above the water surface, so bridges and piers at least
        // this high above the water pass over them.
        public const float HeightAboveSurface = 0.3f;

        // The walls reach this far below the depth limit, so a walker standing at the edge
        // meets them with the whole capsule.
        public const float DepthBelowLimit = 0.5f;

        // Samples are grouped in square tiles of this many samples per side.
        private const int TileSamples = 32;
        private const float RayStartOffset = 0.001f;
        private const float GroundProbeDistance = 512f;
        private const float MinimumSegmentLengthSquared = 0.000001f;
        private const string WorkWorldId = "deep-water-edges";

        private static readonly SimulationVector3 Down = new SimulationVector3(0f, -1f, 0f);

        // Each pair of cell edges joined by a contour segment, per case. A case sets bit n
        // when corner n is deep. The saddle cases 5 and 10 have two variants, chosen by the
        // average of the corners: the second variant keeps the deep corners connected.
        private static readonly int[][] CaseSegments =
        {
            new int[0],
            new[] { 3, 0 },
            new[] { 0, 1 },
            new[] { 3, 1 },
            new[] { 1, 2 },
            new[] { 3, 0, 1, 2 },
            new[] { 0, 2 },
            new[] { 2, 3 },
            new[] { 2, 3 },
            new[] { 0, 2 },
            new[] { 0, 1, 2, 3 },
            new[] { 1, 2 },
            new[] { 3, 1 },
            new[] { 0, 1 },
            new[] { 3, 0 },
            new int[0]
        };

        private static readonly int[] ConnectedFive = { 0, 1, 2, 3 };
        private static readonly int[] ConnectedTen = { 3, 0, 1, 2 };

        // Cell corners in order: (x, z), (x + 1, z), (x + 1, z + 1), (x, z + 1). Edge n joins
        // corner n and corner n + 1.
        private static readonly int[] CornerOffsetX = { 0, 1, 1, 0 };
        private static readonly int[] CornerOffsetZ = { 0, 0, 1, 1 };

        public static List<CollisionTriangle> Build(
            float chunkSize,
            IReadOnlyList<CollisionShape> solidShapes,
            IReadOnlyList<CollisionWaterSurface> waterSurfaces,
            ulong baseStableId)
        {
            var edges = new List<CollisionTriangle>();
            if (waterSurfaces.Count == 0 || !TryGetMovementExtent(solidShapes, out var extent))
            {
                return edges;
            }

            var context = new SampleContext(
                CreateWorld(solidShapes, chunkSize),
                CreateWorld(waterSurfaces, chunkSize),
                waterSurfaces);
            var corners = new Sample[4];
            var cornerDeep = new bool[4];
            foreach (var tile in CollectTiles(waterSurfaces, extent))
            {
                var firstX = tile.X * TileSamples;
                var firstZ = tile.Z * TileSamples;
                for (var cellZ = firstZ; cellZ < firstZ + TileSamples; cellZ++)
                {
                    for (var cellX = firstX; cellX < firstX + TileSamples; cellX++)
                    {
                        // Water beyond the ground of the World, such as a sea that reaches to
                        // the horizon, needs no edges.
                        if (!IsCellInside(cellX, cellZ, extent))
                        {
                            continue;
                        }

                        var caseIndex = 0;
                        for (var corner = 0; corner < 4; corner++)
                        {
                            corners[corner] = context.Get(
                                cellX + CornerOffsetX[corner],
                                cellZ + CornerOffsetZ[corner]);
                            cornerDeep[corner] = corners[corner].Field > 0f;
                            if (cornerDeep[corner])
                            {
                                caseIndex |= 1 << corner;
                            }
                        }

                        var pairs = CaseSegments[caseIndex];
                        if ((caseIndex == 5 || caseIndex == 10) && IsCenterDeep(corners))
                        {
                            pairs = caseIndex == 5 ? ConnectedFive : ConnectedTen;
                        }

                        for (var pair = 0; pair < pairs.Length; pair += 2)
                        {
                            AddSegment(
                                edges,
                                cellX,
                                cellZ,
                                corners,
                                cornerDeep,
                                pairs[pair],
                                pairs[pair + 1],
                                baseStableId);
                        }
                    }
                }
            }

            return edges;
        }

        private static bool IsCellInside(int cellX, int cellZ, CollisionAabb extent)
        {
            return (float)(cellX * SampleSpacing) >= extent.Minimum.X
                && (float)((cellX + 1) * SampleSpacing) <= extent.Maximum.X
                && (float)(cellZ * SampleSpacing) >= extent.Minimum.Z
                && (float)((cellZ + 1) * SampleSpacing) <= extent.Maximum.Z;
        }

        private static bool IsCenterDeep(Sample[] corners)
        {
            var sum = (float)((float)(corners[0].Field + corners[1].Field)
                + (float)(corners[2].Field + corners[3].Field));
            return (float)(sum * 0.25f) > 0f;
        }

        private static void AddSegment(
            List<CollisionTriangle> edges,
            int cellX,
            int cellZ,
            Sample[] corners,
            bool[] cornerDeep,
            int firstEdge,
            int secondEdge,
            ulong baseStableId)
        {
            EdgePoint(cellX, cellZ, corners, cornerDeep, firstEdge, out var firstX, out var firstZ, out var firstSurface);
            EdgePoint(cellX, cellZ, corners, cornerDeep, secondEdge, out var secondX, out var secondZ, out var secondSurface);
            var deltaX = (float)(secondX - firstX);
            var deltaZ = (float)(secondZ - firstZ);
            if ((float)((float)(deltaX * deltaX) + (float)(deltaZ * deltaZ)) < MinimumSegmentLengthSquared)
            {
                return;
            }

            // The wall faces away from the deep side. A segment that cuts off a deep corner
            // has that corner on its deep side; any other segment has every deep corner there.
            var normalX = (float)(-deltaZ);
            var normalZ = deltaX;
            var shared = SharedCorner(firstEdge, secondEdge);
            var deepSide = 0f;
            for (var corner = 0; corner < 4; corner++)
            {
                if (!cornerDeep[corner] || (shared >= 0 && cornerDeep[shared] && corner != shared))
                {
                    continue;
                }

                var cornerX = (float)((cellX + CornerOffsetX[corner]) * SampleSpacing);
                var cornerZ = (float)((cellZ + CornerOffsetZ[corner]) * SampleSpacing);
                var side = (float)((float)(normalX * (float)(cornerX - firstX))
                    + (float)(normalZ * (float)(cornerZ - firstZ)));
                if (Math.Abs(side) > Math.Abs(deepSide))
                {
                    deepSide = side;
                }
            }

            if (deepSide == 0f)
            {
                return;
            }

            var frontX = deepSide > 0f ? (float)(-normalX) : normalX;
            var frontZ = deepSide > 0f ? (float)(-normalZ) : normalZ;
            var firstBottom = new SimulationVector3(firstX, WallBottom(firstSurface), firstZ);
            var firstTop = new SimulationVector3(firstX, (float)(firstSurface + HeightAboveSurface), firstZ);
            var secondBottom = new SimulationVector3(secondX, WallBottom(secondSurface), secondZ);
            var secondTop = new SimulationVector3(secondX, (float)(secondSurface + HeightAboveSurface), secondZ);
            if (!CollisionTriangle.TryCalculateNormal(firstBottom, firstTop, secondTop, out var normal))
            {
                return;
            }

            var reversed = (float)((float)(normal.X * frontX) + (float)(normal.Z * frontZ)) < 0f;
            AddTriangle(edges, firstBottom, firstTop, secondTop, reversed, baseStableId);
            AddTriangle(edges, firstBottom, secondTop, secondBottom, reversed, baseStableId);
        }

        private static float WallBottom(float surface)
        {
            return (float)((float)(surface - WaterRules.BlockDepth) - DepthBelowLimit);
        }

        private static void AddTriangle(
            List<CollisionTriangle> edges,
            SimulationVector3 a,
            SimulationVector3 b,
            SimulationVector3 c,
            bool reversed,
            ulong baseStableId)
        {
            if (!CollisionTriangle.TryCalculateNormal(a, b, c, out _))
            {
                return;
            }

            edges.Add(new CollisionTriangle(
                CollisionWorldCompiler.TriangleStableId(baseStableId, edges.Count),
                CollisionLayers.CharacterMovement | CollisionLayers.DeepWaterEdge,
                a,
                reversed ? c : b,
                reversed ? b : c));
        }

        // The point on a cell edge where the depth reaches the limit, measured from its
        // shallow corner, and the water surface height there.
        private static void EdgePoint(
            int cellX,
            int cellZ,
            Sample[] corners,
            bool[] cornerDeep,
            int edge,
            out float x,
            out float z,
            out float surface)
        {
            var deepCorner = cornerDeep[edge] ? edge : (edge + 1) % 4;
            var shallowCorner = deepCorner == edge ? (edge + 1) % 4 : edge;
            var shallow = corners[shallowCorner];
            var deep = corners[deepCorner];
            var amount = (float)(shallow.Field / (float)(shallow.Field - deep.Field));
            amount = Math.Max(0f, Math.Min(1f, amount));
            var shallowX = (float)((cellX + CornerOffsetX[shallowCorner]) * SampleSpacing);
            var shallowZ = (float)((cellZ + CornerOffsetZ[shallowCorner]) * SampleSpacing);
            var deepX = (float)((cellX + CornerOffsetX[deepCorner]) * SampleSpacing);
            var deepZ = (float)((cellZ + CornerOffsetZ[deepCorner]) * SampleSpacing);
            x = (float)(shallowX + (float)((float)(deepX - shallowX) * amount));
            z = (float)(shallowZ + (float)((float)(deepZ - shallowZ) * amount));

            // A deep sample always has a surface; a dry one outside the water has none.
            surface = shallow.HasSurface
                ? (float)(shallow.Surface + (float)((float)(deep.Surface - shallow.Surface) * amount))
                : deep.Surface;
        }

        // Adjacent edges n and n + 1 share corner n + 1; opposite edges share none.
        private static int SharedCorner(int firstEdge, int secondEdge)
        {
            if ((firstEdge + 1) % 4 == secondEdge)
            {
                return secondEdge;
            }

            if ((secondEdge + 1) % 4 == firstEdge)
            {
                return firstEdge;
            }

            return -1;
        }

        private static bool TryGetMovementExtent(IReadOnlyList<CollisionShape> shapes, out CollisionAabb extent)
        {
            var found = false;
            var minimum = SimulationVector3.Zero;
            var maximum = SimulationVector3.Zero;
            for (var index = 0; index < shapes.Count; index++)
            {
                var shape = shapes[index];
                if ((shape.LayerMask & CollisionLayers.CharacterMovement) == 0)
                {
                    continue;
                }

                minimum = found ? SimulationVector3.Min(minimum, shape.Bounds.Minimum) : shape.Bounds.Minimum;
                maximum = found ? SimulationVector3.Max(maximum, shape.Bounds.Maximum) : shape.Bounds.Maximum;
                found = true;
            }

            extent = found ? new CollisionAabb(minimum, maximum) : default(CollisionAabb);
            return found;
        }

        // The tiles that water covers inside the movement extent, in row order.
        private static List<TileKey> CollectTiles(
            IReadOnlyList<CollisionWaterSurface> waterSurfaces,
            CollisionAabb extent)
        {
            var tileSize = (float)(TileSamples * SampleSpacing);
            var tiles = new HashSet<TileKey>();
            for (var index = 0; index < waterSurfaces.Count; index++)
            {
                var bounds = waterSurfaces[index].Bounds;
                var minimumX = Math.Max(bounds.Minimum.X, extent.Minimum.X);
                var maximumX = Math.Min(bounds.Maximum.X, extent.Maximum.X);
                var minimumZ = Math.Max(bounds.Minimum.Z, extent.Minimum.Z);
                var maximumZ = Math.Min(bounds.Maximum.Z, extent.Maximum.Z);
                if (minimumX > maximumX || minimumZ > maximumZ)
                {
                    continue;
                }

                var firstX = (int)Math.Floor((float)(minimumX / tileSize));
                var lastX = (int)Math.Floor((float)(maximumX / tileSize));
                var firstZ = (int)Math.Floor((float)(minimumZ / tileSize));
                var lastZ = (int)Math.Floor((float)(maximumZ / tileSize));
                for (var z = firstZ; z <= lastZ; z++)
                {
                    for (var x = firstX; x <= lastX; x++)
                    {
                        tiles.Add(new TileKey(x, z));
                    }
                }
            }

            var ordered = new List<TileKey>(tiles);
            ordered.Sort((left, right) => left.Z != right.Z
                ? left.Z.CompareTo(right.Z)
                : left.X.CompareTo(right.X));
            return ordered;
        }

        private static ChunkedStaticCollisionWorld CreateWorld<TShape>(
            IReadOnlyList<TShape> shapes,
            float chunkSize)
            where TShape : CollisionShape
        {
            var world = new ChunkedStaticCollisionWorld(WorkWorldId, WorkWorldId, chunkSize);
            var list = new List<CollisionShape>(shapes.Count);
            for (var index = 0; index < shapes.Count; index++)
            {
                list.Add(shapes[index]);
            }

            foreach (var chunk in CollisionWorldCompiler.BuildChunks(list, chunkSize))
            {
                world.TryLoadChunk(chunk);
            }

            return world;
        }

        private readonly struct TileKey : IEquatable<TileKey>
        {
            public TileKey(int x, int z)
            {
                X = x;
                Z = z;
            }

            public int X { get; }

            public int Z { get; }

            public bool Equals(TileKey other)
            {
                return X == other.X && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                return obj is TileKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (X * 397) ^ Z;
                }
            }
        }

        private readonly struct Sample
        {
            public Sample(float field, bool hasSurface, float surface)
            {
                Field = field;
                HasSurface = hasSurface;
                Surface = surface;
            }

            // The depth minus the depth limit: above zero is deep.
            public float Field { get; }

            public bool HasSurface { get; }

            public float Surface { get; }
        }

        private sealed class SampleContext
        {
            // Dry land counts as water of no depth, and bottomless water as twice the limit.
            private static readonly float DryField = -WaterRules.BlockDepth;
            private static readonly float BottomlessField = WaterRules.BlockDepth;

            private readonly ChunkedStaticCollisionWorld solids;
            private readonly ChunkedStaticCollisionWorld water;
            private readonly float waterTop;
            private readonly float waterProbeDistance;
            private readonly Dictionary<long, Sample> samples = new Dictionary<long, Sample>();
            private readonly CollisionQueryBuffer buffer = new CollisionQueryBuffer();

            public SampleContext(
                ChunkedStaticCollisionWorld solids,
                ChunkedStaticCollisionWorld water,
                IReadOnlyList<CollisionWaterSurface> waterSurfaces)
            {
                this.solids = solids;
                this.water = water;
                var top = float.MinValue;
                var bottom = float.MaxValue;
                for (var index = 0; index < waterSurfaces.Count; index++)
                {
                    top = Math.Max(top, waterSurfaces[index].Bounds.Maximum.Y);
                    bottom = Math.Min(bottom, waterSurfaces[index].Bounds.Minimum.Y);
                }

                waterTop = (float)(top + 1f);
                waterProbeDistance = (float)((float)(waterTop - bottom) + 1f);
            }

            public Sample Get(int sampleX, int sampleZ)
            {
                var key = ((long)sampleX << 32) | (uint)sampleZ;
                if (!samples.TryGetValue(key, out var sample))
                {
                    sample = Measure(
                        (float)(sampleX * SampleSpacing),
                        (float)(sampleZ * SampleSpacing));
                    samples.Add(key, sample);
                }

                return sample;
            }

            private Sample Measure(float x, float z)
            {
                if (!CollisionRaycast.TryRaycast(
                        water,
                        new SimulationVector3(x, waterTop, z),
                        Down,
                        waterProbeDistance,
                        CollisionLayers.Water,
                        buffer,
                        out var surfaceHit))
                {
                    return new Sample(DryField, false, 0f);
                }

                var surface = surfaceHit.Point.Y;
                if (!WaterRules.IsOpenSurface(solids, surfaceHit.Point, buffer))
                {
                    return new Sample(DryField, true, surface);
                }

                if (!CollisionRaycast.TryRaycast(
                        solids,
                        new SimulationVector3(x, (float)(surface + RayStartOffset), z),
                        Down,
                        GroundProbeDistance,
                        CollisionLayers.CharacterMovement,
                        buffer,
                        out var groundHit))
                {
                    return new Sample(BottomlessField, true, surface);
                }

                var depth = (float)(surface - groundHit.Point.Y);
                return new Sample((float)(depth - WaterRules.BlockDepth), true, surface);
            }
        }
    }
}
