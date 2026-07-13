using System;

namespace ShooterMmo.GameSimulation
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
            var localOrigin = box.Rotation.InverseRotate(origin - box.Center);
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
            hit = new CollisionRayHit(
                entryDistance,
                origin + (normalizedDirection * entryDistance),
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
            if (box == null || radius <= 0f || height < radius * 2f)
            {
                return false;
            }

            var bottom = rootPosition + new SimulationVector3(0f, radius, 0f);
            var top = rootPosition + new SimulationVector3(0f, height - radius, 0f);
            var localBottom = box.Rotation.InverseRotate(bottom - box.Center);
            var localTop = box.Rotation.InverseRotate(top - box.Center);
            var segmentDirection = localTop - localBottom;
            var minimum = -box.HalfExtents;
            var maximum = box.HalfExtents;

            var lower = 0f;
            var upper = 1f;
            for (var iteration = 0; iteration < SegmentSearchIterations; iteration++)
            {
                var first = lower + ((upper - lower) / 3f);
                var second = upper - ((upper - lower) / 3f);
                var firstDistance = SquaredDistanceToAabb(
                    localBottom + (segmentDirection * first),
                    minimum,
                    maximum);
                var secondDistance = SquaredDistanceToAabb(
                    localBottom + (segmentDirection * second),
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

            var parameter = (lower + upper) * 0.5f;
            var segmentPoint = localBottom + (segmentDirection * parameter);
            var boxPoint = Clamp(segmentPoint, minimum, maximum);
            var delta = segmentPoint - boxPoint;
            var distanceSquared = delta.LengthSquared;
            if (distanceSquared >= radius * radius)
            {
                return false;
            }

            SimulationVector3 localNormal;
            if (distanceSquared > Epsilon * Epsilon)
            {
                var distance = (float)Math.Sqrt(distanceSquared);
                localNormal = delta / distance;
                depth = radius - distance;
            }
            else
            {
                localNormal = FindNearestExitNormal(segmentPoint, minimum, maximum, out var exitDistance);
                depth = radius + exitDistance;
            }

            if (depth <= 0.00001f)
            {
                depth = 0f;
                return false;
            }

            normal = box.Rotation.Rotate(localNormal).Normalized();
            return true;
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

            var inverse = 1f / direction;
            var first = (minimum - origin) * inverse;
            var second = (maximum - origin) * inverse;
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
            exitDistance = point.X - minimum.X;
            var normal = -SimulationVector3.UnitX;

            SelectCloserExit(maximum.X - point.X, SimulationVector3.UnitX, ref exitDistance, ref normal);
            SelectCloserExit(point.Y - minimum.Y, -SimulationVector3.UnitY, ref exitDistance, ref normal);
            SelectCloserExit(maximum.Y - point.Y, SimulationVector3.UnitY, ref exitDistance, ref normal);
            SelectCloserExit(point.Z - minimum.Z, -SimulationVector3.UnitZ, ref exitDistance, ref normal);
            SelectCloserExit(maximum.Z - point.Z, SimulationVector3.UnitZ, ref exitDistance, ref normal);
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
