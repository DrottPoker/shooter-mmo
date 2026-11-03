using System;

namespace Mmorpg.GameSimulation
{
    public static class CollisionRaycast
    {
        // Finds the nearest shape a ray enters. Equal distances keep the lower stable id.
        public static bool TryRaycast(
            ICollisionWorld collisionWorld,
            SimulationVector3 origin,
            SimulationVector3 direction,
            float maximumDistance,
            uint layerMask,
            CollisionQueryBuffer queryBuffer,
            out CollisionRayHit hit)
        {
            return TryRaycast(
                collisionWorld,
                origin,
                direction,
                maximumDistance,
                layerMask,
                queryBuffer,
                out hit,
                out _);
        }

        // The same, and also the shape the ray enters.
        public static bool TryRaycast(
            ICollisionWorld collisionWorld,
            SimulationVector3 origin,
            SimulationVector3 direction,
            float maximumDistance,
            uint layerMask,
            CollisionQueryBuffer queryBuffer,
            out CollisionRayHit hit,
            out CollisionShape shape)
        {
            shape = null;
            if (collisionWorld == null)
            {
                throw new ArgumentNullException(nameof(collisionWorld));
            }

            if (queryBuffer == null)
            {
                throw new ArgumentNullException(nameof(queryBuffer));
            }

            hit = default(CollisionRayHit);
            if (!SimulationVector3.IsFinite(origin)
                || !SimulationVector3.IsFinite(direction)
                || float.IsNaN(maximumDistance)
                || float.IsInfinity(maximumDistance)
                || maximumDistance <= 0f)
            {
                return false;
            }

            var normalizedDirection = direction.Normalized();
            if (normalizedDirection.LengthSquared <= 0f)
            {
                return false;
            }

            var end = origin + (normalizedDirection * maximumDistance);
            var bounds = new CollisionAabb(
                SimulationVector3.Min(origin, end),
                SimulationVector3.Max(origin, end)).Expanded(0.001f);
            queryBuffer.Clear();
            collisionWorld.QueryShapes(bounds, layerMask, queryBuffer);
            queryBuffer.SortByStableId();

            var found = false;
            for (var index = 0; index < queryBuffer.Shapes.Count; index++)
            {
                // A ray that starts inside a box or on a triangle has no entry and is ignored.
                if (!queryBuffer.Shapes[index].TryRaycast(
                        origin,
                        normalizedDirection,
                        maximumDistance,
                        out var candidate)
                    || candidate.Distance <= 0f
                    || (found && candidate.Distance >= hit.Distance))
                {
                    continue;
                }

                hit = candidate;
                shape = queryBuffer.Shapes[index];
                found = true;
            }

            return found;
        }
    }
}
