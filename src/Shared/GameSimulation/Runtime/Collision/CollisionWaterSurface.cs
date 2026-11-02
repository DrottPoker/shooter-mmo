using System;

namespace Mmorpg.GameSimulation
{
    // One upward-facing triangle of a water body's surface, on the Water layer only. Rays
    // find it from above, so a depth query can read the surface height, but it never
    // pushes a capsule: deep water stops characters through the deep water edges instead.
    public sealed class CollisionWaterSurface : CollisionShape
    {
        public const int MaximumBodyIdLength = 64;

        // The flattest surface tilt that still faces up, about 26 degrees, enough for rapids.
        public const float MinimumNormalY = 0.9f;

        public CollisionWaterSurface(
            ulong stableId,
            string bodyId,
            SimulationVector3 a,
            SimulationVector3 b,
            SimulationVector3 c)
            : base(stableId, CollisionLayers.Water)
        {
            if (!IsValidBodyId(bodyId))
            {
                throw new ArgumentException("Water body id is invalid.", nameof(bodyId));
            }

            if (!SimulationVector3.IsFinite(a)
                || !SimulationVector3.IsFinite(b)
                || !SimulationVector3.IsFinite(c))
            {
                throw new ArgumentException("Water surface vertices are invalid.");
            }

            if (!CollisionTriangle.TryCalculateNormal(a, b, c, out var normal))
            {
                throw new ArgumentException("Water surface triangle is degenerate.");
            }

            if (normal.Y < MinimumNormalY)
            {
                throw new ArgumentException("Water surface triangle does not face up.");
            }

            BodyId = bodyId;
            A = a;
            B = b;
            C = c;
            Normal = normal;
            SetBounds(
                new CollisionAabb(
                    SimulationVector3.Min(SimulationVector3.Min(a, b), c),
                    SimulationVector3.Max(SimulationVector3.Max(a, b), c)),
                "Water surface");
        }

        // The authored water body this triangle belongs to, such as a lake or a river.
        public string BodyId { get; }

        public SimulationVector3 A { get; }

        public SimulationVector3 B { get; }

        public SimulationVector3 C { get; }

        public SimulationVector3 Normal { get; }

        // Lowercase letters, digits, dots, dashes, and underscores, so content can refer to it.
        public static bool IsValidBodyId(string bodyId)
        {
            if (string.IsNullOrEmpty(bodyId) || bodyId.Length > MaximumBodyIdLength)
            {
                return false;
            }

            foreach (var character in bodyId)
            {
                if (!(character >= 'a' && character <= 'z')
                    && !(character >= '0' && character <= '9')
                    && character != '.'
                    && character != '-'
                    && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        public override bool TryRaycast(
            SimulationVector3 origin,
            SimulationVector3 direction,
            float maximumDistance,
            out CollisionRayHit hit)
        {
            return CollisionMath.TryRaycastTriangle(
                origin,
                direction,
                maximumDistance,
                A,
                B,
                C,
                Normal,
                out hit);
        }

        public override bool TryGetCapsulePenetration(
            SimulationVector3 rootPosition,
            float radius,
            float height,
            out SimulationVector3 normal,
            out float depth)
        {
            normal = SimulationVector3.Zero;
            depth = 0f;
            return false;
        }
    }
}
