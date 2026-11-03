using System.Collections.Generic;
using Mmorpg.Client.Worlds;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The center line of a river in world space through its points, straight between them or a
    // curve through them, measured along the ground: where it runs, which way it flows, and what
    // the river is like at every distance from its first point. Heights run straight between the
    // points, so a river falls evenly between them, and the width, depth, speed, and foam turn
    // smoothly from one point's to the next.
    internal sealed class RiverPath
    {
        // The curve is sampled this many times between two points.
        private const int CurveSamples = 24;

        // The direction at a distance is the mean of the line's directions this far before and
        // after it, so the river turns smoothly at a corner of a straight line.
        private const float DirectionReach = 0.5f;

        private readonly List<Vector3> line = new List<Vector3>();
        private readonly List<float> lineDistances = new List<float>();
        private readonly List<RiverPoint> points = new List<RiverPoint>();
        private readonly List<float> pointDistances = new List<float>();

        public RiverPath(IReadOnlyList<RiverPoint> worldPoints, bool curved)
        {
            points.AddRange(worldPoints);
            if (points.Count < 2)
            {
                throw new System.ArgumentException("A river needs at least two points.", nameof(worldPoints));
            }

            for (var index = 0; index < points.Count - 1; index++)
            {
                if (index == 0)
                {
                    AddLinePoint(points[0].Position);
                    pointDistances.Add(0f);
                }

                if (curved)
                {
                    var before = points[Mathf.Max(index - 1, 0)].Position;
                    var from = points[index].Position;
                    var to = points[index + 1].Position;
                    var after = points[Mathf.Min(index + 2, points.Count - 1)].Position;
                    for (var sample = 1; sample < CurveSamples; sample++)
                    {
                        AddLinePoint(CentripetalCatmullRom(before, from, to, after, (float)sample / CurveSamples));
                    }
                }

                AddLinePoint(points[index + 1].Position);
                pointDistances.Add(lineDistances[lineDistances.Count - 1]);
            }
        }

        public float Length
        {
            get { return lineDistances[lineDistances.Count - 1]; }
        }

        public IReadOnlyList<float> PointDistances
        {
            get { return pointDistances; }
        }

        // The point of the line at a distance along it, without its height.
        public Vector2 PlanarAt(float along)
        {
            var segment = SegmentAt(along, out var amount);
            var point = Vector3.Lerp(line[segment], line[segment + 1], amount);
            return new Vector2(point.x, point.z);
        }

        // The way the river flows at a distance along it.
        public Vector2 DirectionAt(float along)
        {
            var direction = SegmentDirection(along - DirectionReach) + SegmentDirection(along + DirectionReach);
            return direction.sqrMagnitude > 0f ? direction.normalized : SegmentDirection(along);
        }

        // What the river is like at a distance along it: the surface's height, and the width,
        // depth, speed, and foam.
        public RiverPoint ValuesAt(float along)
        {
            var index = 0;
            while (index < pointDistances.Count - 2 && along > pointDistances[index + 1])
            {
                index++;
            }

            var span = pointDistances[index + 1] - pointDistances[index];
            var amount = span > 0f ? Mathf.Clamp01((along - pointDistances[index]) / span) : 0f;
            var smooth = WorldBuilderGeometry.Smooth01(amount);
            var from = points[index];
            var to = points[index + 1];
            var planar = PlanarAt(along);
            return new RiverPoint
            {
                Position = new Vector3(planar.x, Mathf.Lerp(from.Position.y, to.Position.y, amount), planar.y),
                Width = Mathf.Lerp(from.Width, to.Width, smooth),
                Depth = Mathf.Lerp(from.Depth, to.Depth, smooth),
                Speed = Mathf.Lerp(from.Speed, to.Speed, smooth),
                Foam = Mathf.Lerp(from.Foam, to.Foam, smooth)
            };
        }

        // The distance from a point to the line in X and Z, and how far along it the nearest point lies.
        public float DistanceTo(Vector2 point, out float along)
        {
            var best = float.MaxValue;
            along = 0f;
            for (var index = 0; index < line.Count - 1; index++)
            {
                var from = new Vector2(line[index].x, line[index].z);
                var to = new Vector2(line[index + 1].x, line[index + 1].z);
                var segment = to - from;
                var lengthSquared = segment.sqrMagnitude;
                var amount = lengthSquared > 0f ? Mathf.Clamp01(Vector2.Dot(point - from, segment) / lengthSquared) : 0f;
                var distance = Vector2.Distance(point, from + (segment * amount));
                if (distance < best)
                {
                    best = distance;
                    along = Mathf.Lerp(lineDistances[index], lineDistances[index + 1], amount);
                }
            }

            return best;
        }

        private void AddLinePoint(Vector3 point)
        {
            lineDistances.Add(line.Count == 0
                ? 0f
                : lineDistances[lineDistances.Count - 1] + Vector2.Distance(
                    new Vector2(line[line.Count - 1].x, line[line.Count - 1].z),
                    new Vector2(point.x, point.z)));
            line.Add(point);
        }

        private int SegmentAt(float along, out float amount)
        {
            var segment = 0;
            while (segment < line.Count - 2 && along > lineDistances[segment + 1])
            {
                segment++;
            }

            var span = lineDistances[segment + 1] - lineDistances[segment];
            amount = span > 0f ? (along - lineDistances[segment]) / span : 0f;
            return segment;
        }

        private Vector2 SegmentDirection(float along)
        {
            var segment = SegmentAt(Mathf.Clamp(along, 0f, Length), out _);
            var delta = line[segment + 1] - line[segment];
            var planar = new Vector2(delta.x, delta.z);
            return planar.sqrMagnitude > 0f ? planar.normalized : Vector2.up;
        }

        // A centripetal Catmull-Rom curve from the second point to the third, which never loops
        // or overshoots at a sharp turn.
        private static Vector3 CentripetalCatmullRom(Vector3 before, Vector3 from, Vector3 to, Vector3 after, float amount)
        {
            var t0 = 0f;
            var t1 = t0 + Knot(before, from);
            var t2 = t1 + Knot(from, to);
            var t3 = t2 + Knot(to, after);
            var t = Mathf.Lerp(t1, t2, amount);
            var a1 = Blend(before, from, t0, t1, t);
            var a2 = Blend(from, to, t1, t2, t);
            var a3 = Blend(to, after, t2, t3, t);
            var b1 = Blend(a1, a2, t0, t2, t);
            var b2 = Blend(a2, a3, t1, t3, t);
            return Blend(b1, b2, t1, t2, t);
        }

        private static float Knot(Vector3 from, Vector3 to)
        {
            return Mathf.Max(Mathf.Sqrt(Vector3.Distance(from, to)), 0.0001f);
        }

        private static Vector3 Blend(Vector3 from, Vector3 to, float start, float end, float t)
        {
            return end - start <= 0f ? from : Vector3.LerpUnclamped(from, to, (t - start) / (end - start));
        }
    }
}
