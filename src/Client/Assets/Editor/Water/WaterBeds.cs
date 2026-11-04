using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Worlds;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The ground that water gives the terrain below it: the bed under the water and the bank that
    // rises from its shore to the ground around it. A lake's floor and a sea's fall with the
    // distance from the shore down to their deepest, a river's channel is deepest in its middle,
    // and a waterfall digs a pool where it lands. A terrain is lowered to the lowest of them and
    // never raised, so water above the ground, such as a stream on rock, leaves it as it is.
    internal abstract class WaterBed
    {
        // The area in world X and Z outside which the bed leaves the ground as it is.
        public abstract Rect Bounds { get; }

        // The height the bed gives the ground at a point where the ground without water lies at
        // the given height, or infinity where the bed does not reach.
        public abstract float HeightAt(Vector2 point, float ground);

        // The beds of every complete water shape of the scene that carves its bed.
        public static List<WaterBed> Collect(WaterScene water)
        {
            var beds = new List<WaterBed>();
            foreach (var lake in water.Lakes)
            {
                if (lake.Lake.CarvesBed && lake.Outline.Length >= 3)
                {
                    beds.Add(new OpenWaterBed(lake.Outline, new bool[lake.Outline.Length], lake.Level, lake.Lake.FloorSlope, lake.Lake.MaximumDepth, lake.Lake.BankWidth));
                }
            }

            foreach (var sea in water.Seas)
            {
                if (sea.Sea.CarvesBed && sea.Outline.Length >= 3)
                {
                    beds.Add(new OpenWaterBed(sea.Outline, OpenEdgesOf(sea, water.WorldEdge), sea.Level, sea.Sea.FloorSlope, sea.Sea.MaximumDepth, sea.Sea.BankWidth));
                }
            }

            foreach (var river in water.Rivers)
            {
                if (river.River.CarvesBed)
                {
                    beds.Add(new RiverBed(river));
                }
            }

            foreach (var waterfall in water.Waterfalls)
            {
                var shape = waterfall.Shape;
                if (shape.PoolRadius > 0f && shape.PoolDepth > 0f)
                {
                    beds.Add(new PoolBed(waterfall.Fall.Landing, waterfall.Fall.LandingLevel, shape.PoolRadius, shape.PoolDepth));
                }
            }

            return beds;
        }

        // Which edges of a sea's outline are open, each edge ending at its point: those along the
        // World's edge on its horizon sides, where the sea runs on and its floor keeps falling.
        // The others are its shore.
        private static bool[] OpenEdgesOf(SeaModel sea, Rect? worldEdge)
        {
            var outline = sea.Outline;
            var open = new bool[outline.Length];
            for (int index = 0, previous = outline.Length - 1; index < outline.Length; previous = index++)
            {
                open[index] = worldEdge.HasValue && IsOpen(outline[previous], outline[index], sea.Sea.Horizon, worldEdge.Value);
            }

            return open;
        }

        // Whether an edge lies on the World's edge, or beyond it, on one of the horizon sides.
        private static bool IsOpen(Vector2 from, Vector2 to, HorizonSides horizon, Rect edge)
        {
            const float tolerance = 0.5f;
            return ((horizon & HorizonSides.West) != 0 && from.x <= edge.xMin + tolerance && to.x <= edge.xMin + tolerance)
                || ((horizon & HorizonSides.East) != 0 && from.x >= edge.xMax - tolerance && to.x >= edge.xMax - tolerance)
                || ((horizon & HorizonSides.South) != 0 && from.y <= edge.yMin + tolerance && to.y <= edge.yMin + tolerance)
                || ((horizon & HorizonSides.North) != 0 && from.y >= edge.yMax - tolerance && to.y >= edge.yMax - tolerance);
        }

        protected static Rect Expand(Rect rect, float amount)
        {
            return Rect.MinMaxRect(rect.xMin - amount, rect.yMin - amount, rect.xMax + amount, rect.yMax + amount);
        }

        // The ground between the shore, at the water's level, and the end of the bank, at the
        // ground around the water.
        protected static float Bank(float level, float ground, float distance, float width)
        {
            return width > 0f && distance < width ? level + ((ground - level) * (distance / width)) : float.PositiveInfinity;
        }
    }

    // A lake or a sea: the floor falls from its level at the shore by the slope down to its
    // deepest, and the bank rises from the shore to the ground outside. A sea's open edges are no
    // shore: its floor keeps falling up to them and beyond them, where a point lies nearer to an
    // open edge than to any shore.
    internal sealed class OpenWaterBed : WaterBed
    {
        private readonly Vector2[] outline;
        private readonly bool[] open;
        private readonly float level;
        private readonly float slope;
        private readonly float deepest;
        private readonly float bank;
        private readonly Rect bounds;

        // Each edge of the outline ends at its point, and the open flags say which are open.
        public OpenWaterBed(Vector2[] outline, bool[] open, float level, float slope, float deepest, float bank)
        {
            this.outline = outline;
            this.open = open;
            this.level = level;
            this.slope = slope;
            this.deepest = deepest;
            this.bank = bank;
            bounds = Expand(WorldBuilderGeometry.Bounds(outline), bank);
        }

        public override Rect Bounds
        {
            get { return bounds; }
        }

        // Painting asks this for many points while a shape is dragged, so one pass over the edges
        // finds the nearest shore and open edge, by their squared distances, and whether the
        // point lies inside the outline.
        public override float HeightAt(Vector2 point, float ground)
        {
            if (!bounds.Contains(point))
            {
                return float.PositiveInfinity;
            }

            var shore = float.MaxValue;
            var openEdge = float.MaxValue;
            var inside = false;
            for (int index = 0, previous = outline.Length - 1; index < outline.Length; previous = index++)
            {
                var from = outline[previous];
                var to = outline[index];
                var squared = SquaredDistanceToSegment(from, to, point);
                if (open[index])
                {
                    openEdge = Mathf.Min(openEdge, squared);
                }
                else
                {
                    shore = Mathf.Min(shore, squared);
                }

                if ((to.y > point.y) != (from.y > point.y)
                    && point.x < ((from.x - to.x) * (point.y - to.y) / (from.y - to.y)) + to.x)
                {
                    inside = !inside;
                }
            }

            inside |= openEdge < shore;
            shore = Mathf.Sqrt(shore);
            return inside ? level - Mathf.Min(deepest, slope * shore) : Bank(level, ground, shore, bank);
        }

        private static float SquaredDistanceToSegment(Vector2 from, Vector2 to, Vector2 point)
        {
            var segmentX = to.x - from.x;
            var segmentY = to.y - from.y;
            var lengthSquared = (segmentX * segmentX) + (segmentY * segmentY);
            var amount = lengthSquared > 0f
                ? Mathf.Clamp01((((point.x - from.x) * segmentX) + ((point.y - from.y) * segmentY)) / lengthSquared)
                : 0f;
            var offsetX = point.x - (from.x + (segmentX * amount));
            var offsetY = point.y - (from.y + (segmentY * amount));
            return (offsetX * offsetX) + (offsetY * offsetY);
        }
    }

    // A river: the channel is as deep as the river in its middle and rises smoothly to its
    // surface at the edge of its water, and the banks rise from there to the ground beside it.
    internal sealed class RiverBed : WaterBed
    {
        private readonly RiverPath path;
        private readonly float bank;
        private readonly Rect bounds;

        // How far from its line the bed reaches where the river is widest.
        private readonly float reach;

        public RiverBed(RiverModel river)
        {
            path = river.Path;
            bank = river.River.BankWidth;
            var widest = river.Points.Max(point => point.Width);
            reach = (widest * 0.5f) + bank;

            // The line itself, since a curve through the points can bulge far past them.
            bounds = Expand(path.Bounds, reach);
        }

        public override Rect Bounds
        {
            get { return bounds; }
        }

        public override float HeightAt(Vector2 point, float ground)
        {
            if (!bounds.Contains(point))
            {
                return float.PositiveInfinity;
            }

            var distance = path.DistanceTo(point, out var along);
            if (distance >= reach)
            {
                return float.PositiveInfinity;
            }

            var values = path.ValuesAt(along);
            var halfWidth = values.Width * 0.5f;
            var surface = values.Position.y;
            return distance < halfWidth
                ? surface - (values.Depth * (1f - WorldBuilderGeometry.Smooth01(distance / halfWidth)))
                : Bank(surface, ground, distance - halfWidth, bank);
        }
    }

    // A waterfall's pool: deepest where the water lands and rising smoothly to the floor around.
    internal sealed class PoolBed : WaterBed
    {
        private readonly Vector2 center;
        private readonly float level;
        private readonly float radius;
        private readonly float depth;

        public PoolBed(Vector2 center, float level, float radius, float depth)
        {
            this.center = center;
            this.level = level;
            this.radius = radius;
            this.depth = depth;
        }

        public override Rect Bounds
        {
            get { return new Rect(center.x - radius, center.y - radius, radius * 2f, radius * 2f); }
        }

        public override float HeightAt(Vector2 point, float ground)
        {
            var distance = Vector2.Distance(point, center);
            return distance < radius
                ? level - (depth * (1f - WorldBuilderGeometry.Smooth01(distance / radius)))
                : float.PositiveInfinity;
        }
    }
}
