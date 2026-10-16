using System;

namespace Mmorpg.GameSimulation
{
    public readonly struct CollisionRayHit
    {
        public CollisionRayHit(float distance, SimulationVector3 point, SimulationVector3 normal)
        {
            Distance = distance;
            Point = point;
            Normal = normal;
        }

        public float Distance { get; }

        public SimulationVector3 Point { get; }

        public SimulationVector3 Normal { get; }
    }

    public static class CollisionMath
    {
        private const float Epsilon = 0.000001f;
        private const int SegmentSearchIterations = 24;

        // A small barycentric tolerance makes a ray through an edge shared by two
        // triangles hit at least one of them, so rays never fall through mesh seams.
        private const float MinimumBarycentric = -0.00001f;
        private const float MaximumBarycentric = 1.00001f;

        // A capsule axis that crosses a triangle plane this close to the triangle crosses it.
        private const float CrossingToleranceSquared = 0.00000001f;

        public static bool TryRaycastBox(
            SimulationVector3 origin,
            SimulationVector3 direction,
            float maximumDistance,
            CollisionBox box,
            out CollisionRayHit hit)
        {
            hit = default(CollisionRayHit);
            if (box == null
                || !SimulationVector3.IsFinite(origin)
                || !SimulationVector3.IsFinite(direction)
                || !IsFinite(maximumDistance)
                || maximumDistance <= 0f)
            {
                return false;
            }

            var directionLength = direction.Length;
            if (directionLength <= Epsilon)
            {
                return false;
            }

            var normalizedDirection = direction / directionLength;
            var offset = origin - box.Center;
            var localOrigin = box.Rotation.InverseRotate(offset);
            var localDirection = box.Rotation.InverseRotate(normalizedDirection);
            var minimum = -box.HalfExtents;
            var maximum = box.HalfExtents;
            var entryDistance = 0f;
            var exitDistance = maximumDistance;
            var entryNormal = SimulationVector3.Zero;

            if (!ClipRayAxis(
                    localOrigin.X,
                    localDirection.X,
                    minimum.X,
                    maximum.X,
                    SimulationVector3.UnitX,
                    ref entryDistance,
                    ref exitDistance,
                    ref entryNormal)
                || !ClipRayAxis(
                    localOrigin.Y,
                    localDirection.Y,
                    minimum.Y,
                    maximum.Y,
                    SimulationVector3.UnitY,
                    ref entryDistance,
                    ref exitDistance,
                    ref entryNormal)
                || !ClipRayAxis(
                    localOrigin.Z,
                    localDirection.Z,
                    minimum.Z,
                    maximum.Z,
                    SimulationVector3.UnitZ,
                    ref entryDistance,
                    ref exitDistance,
                    ref entryNormal)
                || entryDistance < 0f
                || entryDistance > maximumDistance)
            {
                return false;
            }

            var worldNormal = box.Rotation.Rotate(entryNormal).Normalized();
            var travelled = normalizedDirection * entryDistance;
            hit = new CollisionRayHit(
                entryDistance,
                origin + travelled,
                worldNormal);
            return true;
        }

        public static bool TryGetCapsuleBoxPenetration(
            SimulationVector3 rootPosition,
            float radius,
            float height,
            CollisionBox box,
            out SimulationVector3 normal,
            out float depth)
        {
            normal = SimulationVector3.Zero;
            depth = 0f;
            if (box == null || radius <= 0f || height < (float)(radius * 2f))
            {
                return false;
            }

            var bottom = rootPosition + new SimulationVector3(0f, radius, 0f);
            var top = rootPosition + new SimulationVector3(0f, (float)(height - radius), 0f);
            var bottomOffset = bottom - box.Center;
            var topOffset = top - box.Center;
            var localBottom = box.Rotation.InverseRotate(bottomOffset);
            var localTop = box.Rotation.InverseRotate(topOffset);
            var segmentDirection = localTop - localBottom;
            var minimum = -box.HalfExtents;
            var maximum = box.HalfExtents;

            var lower = 0f;
            var upper = 1f;
            for (var iteration = 0; iteration < SegmentSearchIterations; iteration++)
            {
                var third = (float)((float)(upper - lower) / 3f);
                var first = (float)(lower + third);
                var second = (float)(upper - third);
                var firstOffset = segmentDirection * first;
                var secondOffset = segmentDirection * second;
                var firstDistance = SquaredDistanceToAabb(
                    localBottom + firstOffset,
                    minimum,
                    maximum);
                var secondDistance = SquaredDistanceToAabb(
                    localBottom + secondOffset,
                    minimum,
                    maximum);
                if (firstDistance <= secondDistance)
                {
                    upper = second;
                }
                else
                {
                    lower = first;
                }
            }

            var parameter = (float)((float)(lower + upper) * 0.5f);
            var segmentOffset = segmentDirection * parameter;
            var segmentPoint = localBottom + segmentOffset;
            var boxPoint = Clamp(segmentPoint, minimum, maximum);
            var delta = segmentPoint - boxPoint;
            var distanceSquared = delta.LengthSquared;
            if (distanceSquared >= (float)(radius * radius))
            {
                return false;
            }

            SimulationVector3 localNormal;
            if (distanceSquared > Epsilon * Epsilon)
            {
                var distance = (float)Math.Sqrt(distanceSquared);
                localNormal = delta / distance;
                depth = (float)(radius - distance);
            }
            else
            {
                localNormal = FindNearestExitNormal(segmentPoint, minimum, maximum, out var exitDistance);
                depth = (float)(radius + exitDistance);
            }

            if (depth <= 0.00001f)
            {
                depth = 0f;
                return false;
            }

            normal = box.Rotation.Rotate(localNormal).Normalized();
            return true;
        }

        // Moller-Trumbore intersection that only accepts rays travelling against the
        // front normal of the triangle.
        public static bool TryRaycastTriangle(
            SimulationVector3 origin,
            SimulationVector3 direction,
            float maximumDistance,
            CollisionTriangle triangle,
            out CollisionRayHit hit)
        {
            hit = default(CollisionRayHit);
            if (triangle == null
                || !SimulationVector3.IsFinite(origin)
                || !SimulationVector3.IsFinite(direction)
                || !IsFinite(maximumDistance)
                || maximumDistance <= 0f)
            {
                return false;
            }

            var directionLength = direction.Length;
            if (directionLength <= Epsilon)
            {
                return false;
            }

            var normalizedDirection = direction / directionLength;
            if (SimulationVector3.Dot(normalizedDirection, triangle.Normal) >= -Epsilon)
            {
                return false;
            }

            var firstEdge = triangle.B - triangle.A;
            var secondEdge = triangle.C - triangle.A;
            var directionCrossEdge = SimulationVector3.Cross(normalizedDirection, secondEdge);
            var determinant = SimulationVector3.Dot(firstEdge, directionCrossEdge);
            if (!(determinant > 0f))
            {
                return false;
            }

            var inverse = (float)(1f / determinant);
            var offset = origin - triangle.A;
            var u = (float)(SimulationVector3.Dot(offset, directionCrossEdge) * inverse);
            if (u < MinimumBarycentric || u > MaximumBarycentric)
            {
                return false;
            }

            var offsetCrossEdge = SimulationVector3.Cross(offset, firstEdge);
            var v = (float)(SimulationVector3.Dot(normalizedDirection, offsetCrossEdge) * inverse);
            if (v < MinimumBarycentric || (float)(u + v) > MaximumBarycentric)
            {
                return false;
            }

            var distance = (float)(SimulationVector3.Dot(secondEdge, offsetCrossEdge) * inverse);
            if (distance < 0f || distance > maximumDistance)
            {
                return false;
            }

            var travelled = normalizedDirection * distance;
            hit = new CollisionRayHit(distance, origin + travelled, triangle.Normal);
            return true;
        }

        // Only the front of a triangle collides. A capsule axis that crosses the triangle is
        // pushed out to the front, and a capsule whose closest point lies behind the
        // triangle passes it.
        public static bool TryGetCapsuleTrianglePenetration(
            SimulationVector3 rootPosition,
            float radius,
            float height,
            CollisionTriangle triangle,
            out SimulationVector3 normal,
            out float depth)
        {
            normal = SimulationVector3.Zero;
            depth = 0f;
            if (triangle == null || radius <= 0f || height < (float)(radius * 2f))
            {
                return false;
            }

            var bottom = rootPosition + new SimulationVector3(0f, radius, 0f);
            var top = rootPosition + new SimulationVector3(0f, (float)(height - radius), 0f);
            var bottomOffset = bottom - triangle.A;
            var topOffset = top - triangle.A;
            var bottomHeight = SimulationVector3.Dot(bottomOffset, triangle.Normal);
            var topHeight = SimulationVector3.Dot(topOffset, triangle.Normal);
            if ((bottomHeight < 0f && topHeight < 0f)
                || (bottomHeight >= radius && topHeight >= radius))
            {
                return false;
            }

            var segmentDirection = top - bottom;
            if ((bottomHeight < 0f) != (topHeight < 0f))
            {
                var crossingAmount = (float)(bottomHeight / (float)(bottomHeight - topHeight));
                var crossingOffset = segmentDirection * crossingAmount;
                if (SquaredDistanceToTriangle(bottom + crossingOffset, triangle) <= CrossingToleranceSquared)
                {
                    normal = triangle.Normal;
                    depth = (float)(radius - Math.Min(bottomHeight, topHeight));
                    return true;
                }
            }

            var lower = 0f;
            var upper = 1f;
            for (var iteration = 0; iteration < SegmentSearchIterations; iteration++)
            {
                var third = (float)((float)(upper - lower) / 3f);
                var first = (float)(lower + third);
                var second = (float)(upper - third);
                var firstOffset = segmentDirection * first;
                var secondOffset = segmentDirection * second;
                var firstDistance = SquaredDistanceToTriangle(bottom + firstOffset, triangle);
                var secondDistance = SquaredDistanceToTriangle(bottom + secondOffset, triangle);
                if (firstDistance <= secondDistance)
                {
                    upper = second;
                }
                else
                {
                    lower = first;
                }
            }

            var parameter = (float)((float)(lower + upper) * 0.5f);
            var segmentOffset = segmentDirection * parameter;
            var segmentPoint = bottom + segmentOffset;
            var delta = segmentPoint - ClosestPointOnTriangle(segmentPoint, triangle);
            var distanceSquared = delta.LengthSquared;
            if (distanceSquared >= (float)(radius * radius))
            {
                return false;
            }

            if (distanceSquared > Epsilon * Epsilon)
            {
                var pointOffset = segmentPoint - triangle.A;
                if (SimulationVector3.Dot(pointOffset, triangle.Normal) <= 0f)
                {
                    return false;
                }

                var distance = (float)Math.Sqrt(distanceSquared);
                normal = delta / distance;
                depth = (float)(radius - distance);
            }
            else
            {
                // The axis touches the triangle without crossing it.
                normal = triangle.Normal;
                depth = radius;
            }

            if (depth <= 0.00001f)
            {
                normal = SimulationVector3.Zero;
                depth = 0f;
                return false;
            }

            return true;
        }

        private static float SquaredDistanceToTriangle(
            SimulationVector3 point,
            CollisionTriangle triangle)
        {
            var closest = ClosestPointOnTriangle(point, triangle);
            return (point - closest).LengthSquared;
        }

        // Closest point on a triangle by Voronoi regions, as in Real-Time Collision
        // Detection by Christer Ericson.
        private static SimulationVector3 ClosestPointOnTriangle(
            SimulationVector3 point,
            CollisionTriangle triangle)
        {
            var a = triangle.A;
            var b = triangle.B;
            var c = triangle.C;
            var ab = b - a;
            var ac = c - a;
            var ap = point - a;
            var d1 = SimulationVector3.Dot(ab, ap);
            var d2 = SimulationVector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                return a;
            }

            var bp = point - b;
            var d3 = SimulationVector3.Dot(ab, bp);
            var d4 = SimulationVector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                return b;
            }

            var vc = (float)((float)(d1 * d4) - (float)(d3 * d2));
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                var edgeAmount = (float)(d1 / (float)(d1 - d3));
                return a + (ab * edgeAmount);
            }

            var cp = point - c;
            var d5 = SimulationVector3.Dot(ab, cp);
            var d6 = SimulationVector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                return c;
            }

            var vb = (float)((float)(d5 * d2) - (float)(d1 * d6));
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                var edgeAmount = (float)(d2 / (float)(d2 - d6));
                return a + (ac * edgeAmount);
            }

            var va = (float)((float)(d3 * d6) - (float)(d5 * d4));
            var towardC = (float)(d4 - d3);
            var awayFromC = (float)(d5 - d6);
            if (va <= 0f && towardC >= 0f && awayFromC >= 0f)
            {
                var edgeAmount = (float)(towardC / (float)(towardC + awayFromC));
                return b + ((c - b) * edgeAmount);
            }

            var inverse = (float)(1f / (float)((float)(va + vb) + vc));
            var alongB = (float)(vb * inverse);
            var alongC = (float)(vc * inverse);
            return a + (ab * alongB) + (ac * alongC);
        }

        private static bool ClipRayAxis(
            float origin,
            float direction,
            float minimum,
            float maximum,
            SimulationVector3 positiveAxis,
            ref float entryDistance,
            ref float exitDistance,
            ref SimulationVector3 entryNormal)
        {
            if (Math.Abs(direction) <= Epsilon)
            {
                return origin >= minimum && origin <= maximum;
            }

            var inverse = (float)(1f / direction);
            var first = (float)((float)(minimum - origin) * inverse);
            var second = (float)((float)(maximum - origin) * inverse);
            var firstNormal = -positiveAxis;
            if (first > second)
            {
                var temporary = first;
                first = second;
                second = temporary;
                firstNormal = positiveAxis;
            }

            if (first > entryDistance)
            {
                entryDistance = first;
                entryNormal = firstNormal;
            }

            exitDistance = Math.Min(exitDistance, second);
            return entryDistance <= exitDistance;
        }

        private static float SquaredDistanceToAabb(
            SimulationVector3 point,
            SimulationVector3 minimum,
            SimulationVector3 maximum)
        {
            var closest = Clamp(point, minimum, maximum);
            return (point - closest).LengthSquared;
        }

        private static SimulationVector3 Clamp(
            SimulationVector3 value,
            SimulationVector3 minimum,
            SimulationVector3 maximum)
        {
            return new SimulationVector3(
                Math.Max(minimum.X, Math.Min(maximum.X, value.X)),
                Math.Max(minimum.Y, Math.Min(maximum.Y, value.Y)),
                Math.Max(minimum.Z, Math.Min(maximum.Z, value.Z)));
        }

        private static SimulationVector3 FindNearestExitNormal(
            SimulationVector3 point,
            SimulationVector3 minimum,
            SimulationVector3 maximum,
            out float exitDistance)
        {
            exitDistance = (float)(point.X - minimum.X);
            var normal = -SimulationVector3.UnitX;

            SelectCloserExit((float)(maximum.X - point.X), SimulationVector3.UnitX, ref exitDistance, ref normal);
            SelectCloserExit((float)(point.Y - minimum.Y), -SimulationVector3.UnitY, ref exitDistance, ref normal);
            SelectCloserExit((float)(maximum.Y - point.Y), SimulationVector3.UnitY, ref exitDistance, ref normal);
            SelectCloserExit((float)(point.Z - minimum.Z), -SimulationVector3.UnitZ, ref exitDistance, ref normal);
            SelectCloserExit((float)(maximum.Z - point.Z), SimulationVector3.UnitZ, ref exitDistance, ref normal);
            return normal;
        }

        private static void SelectCloserExit(
            float candidateDistance,
            SimulationVector3 candidateNormal,
            ref float distance,
            ref SimulationVector3 normal)
        {
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                normal = candidateNormal;
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
