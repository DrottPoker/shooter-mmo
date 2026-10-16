using System;

namespace Mmorpg.GameSimulation
{
    public static class CollisionRaycast
    {
        // Finds the nearest box a ray enters. Equal distances keep the lower stable id.
        public static bool TryRaycast(
            ICollisionWorld collisionWorld,
            SimulationVector3 origin,
            SimulationVector3 direction,
            float maximumDistance,
            uint layerMask,
            CollisionQueryBuffer queryBuffer,
            out CollisionRayHit hit)
        {
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
            collisionWorld.QueryBoxes(bounds, layerMask, queryBuffer);
            queryBuffer.SortByStableId();

            var found = false;
            for (var index = 0; index < queryBuffer.Boxes.Count; index++)
            {
                // A ray that starts inside a box has no entry face and is ignored there.
                if (!CollisionMath.TryRaycastBox(
                        origin,
                        normalizedDirection,
                        maximumDistance,
                        queryBuffer.Boxes[index],
                        out var candidate)
                    || candidate.Distance <= 0f
                    || (found && candidate.Distance >= hit.Distance))
                {
                    continue;
                }

                hit = candidate;
                found = true;
            }

            return found;
        }
    }
}
