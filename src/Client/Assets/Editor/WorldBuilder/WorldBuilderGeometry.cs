using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Plane geometry in world X and Z for the World Builder: closed outlines and center lines.
    internal static class WorldBuilderGeometry
    {
        // Whether the point lies inside the closed outline, by the even-odd rule.
        public static bool Contains(IReadOnlyList<Vector2> outline, Vector2 point)
        {
            var inside = false;
            for (int index = 0, previous = outline.Count - 1; index < outline.Count; previous = index++)
            {
                var a = outline[index];
                var b = outline[previous];
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < ((b.x - a.x) * (point.y - a.y) / (b.y - a.y)) + a.x)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        // The distance from the point to the nearest edge of the closed outline.
        public static float DistanceToOutline(IReadOnlyList<Vector2> outline, Vector2 point)
        {
            var best = float.MaxValue;
            for (int index = 0, previous = outline.Count - 1; index < outline.Count; previous = index++)
            {
                best = Mathf.Min(best, DistanceToSegment(outline[previous], outline[index], point));
            }

            return best;
        }

        // How far inside the outline the point lies: positive inside, negative outside.
        public static float SignedDepth(IReadOnlyList<Vector2> outline, Vector2 point)
        {
            var distance = DistanceToOutline(outline, point);
            return Contains(outline, point) ? distance : -distance;
        }

        public static float DistanceToSegment(Vector2 from, Vector2 to, Vector2 point)
        {
            var segment = to - from;
            var lengthSquared = segment.sqrMagnitude;
            var amount = lengthSquared > 0f ? Mathf.Clamp01(Vector2.Dot(point - from, segment) / lengthSquared) : 0f;
            return Vector2.Distance(point, from + (segment * amount));
        }

        public static Rect Bounds(IReadOnlyList<Vector2> points)
        {
            var minimum = new Vector2(float.MaxValue, float.MaxValue);
            var maximum = new Vector2(float.MinValue, float.MinValue);
            foreach (var point in points)
            {
                minimum = Vector2.Min(minimum, point);
                maximum = Vector2.Max(maximum, point);
            }

            return Rect.MinMaxRect(minimum.x, minimum.y, maximum.x, maximum.y);
        }

        public static float Smooth01(float value)
        {
            var clamped = Mathf.Clamp01(value);
            return clamped * clamped * (3f - (2f * clamped));
        }

        public static Vector2 Planar(Vector3 point)
        {
            return new Vector2(point.x, point.z);
        }
    }
}
