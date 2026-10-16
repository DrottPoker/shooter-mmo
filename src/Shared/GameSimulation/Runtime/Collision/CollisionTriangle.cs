using System;

namespace Mmorpg.GameSimulation
{
    // A one-sided triangle from a baked mesh or heightfield. Its front is the side from
    // which the vertices A, B, C run clockwise, Unity's front-face winding, and Normal
    // points out of the front. Rays and capsules collide only from the front, so a tunnel
    // ceiling that faces down can lie below walkable terrain that faces up.
    public sealed class CollisionTriangle : CollisionShape
    {
        // Twice the area in square meters below which a triangle has no stable normal.
        public const float MinimumDoubleArea = 0.000001f;

        public CollisionTriangle(
            ulong stableId,
            uint layerMask,
            SimulationVector3 a,
            SimulationVector3 b,
            SimulationVector3 c)
            : base(stableId, layerMask)
        {
            if (!SimulationVector3.IsFinite(a)
                || !SimulationVector3.IsFinite(b)
                || !SimulationVector3.IsFinite(c))
            {
                throw new ArgumentException("Collision triangle vertices are invalid.");
            }

            if (!TryCalculateNormal(a, b, c, out var normal))
            {
                throw new ArgumentException("Collision triangle is degenerate.");
            }

            A = a;
            B = b;
            C = c;
            Normal = normal;
            SetBounds(
                new CollisionAabb(
                    SimulationVector3.Min(SimulationVector3.Min(a, b), c),
                    SimulationVector3.Max(SimulationVector3.Max(a, b), c)),
                "Collision triangle");
        }

        public SimulationVector3 A { get; }

        public SimulationVector3 B { get; }

        public SimulationVector3 C { get; }

        public SimulationVector3 Normal { get; }

        // The compiler skips degenerate source triangles with the same test.
        public static bool TryCalculateNormal(
            SimulationVector3 a,
            SimulationVector3 b,
            SimulationVector3 c,
            out SimulationVector3 normal)
        {
            var cross = SimulationVector3.Cross(b - a, c - a);
            var doubleArea = cross.Length;
            if (!(doubleArea > MinimumDoubleArea))
            {
                normal = SimulationVector3.Zero;
                return false;
            }

            normal = cross / doubleArea;
            return true;
        }

        public override bool TryRaycast(
            SimulationVector3 origin,
            SimulationVector3 direction,
            float maximumDistance,
            out CollisionRayHit hit)
        {
            return CollisionMath.TryRaycastTriangle(origin, direction, maximumDistance, this, out hit);
        }

        public override bool TryGetCapsulePenetration(
            SimulationVector3 rootPosition,
            float radius,
            float height,
            out SimulationVector3 normal,
            out float depth)
        {
            return CollisionMath.TryGetCapsuleTrianglePenetration(
                rootPosition,
                radius,
                height,
                this,
                out normal,
                out depth);
        }
    }
}
