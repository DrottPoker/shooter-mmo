namespace Mmorpg.GameSimulation
{
    // How water affects movement. Characters walk at full speed in shallow water and slower
    // from the wading depth on. The collision compiler bakes deep water edges where the water
    // gets deeper than the block depth, so nobody walks into deep water. The values are part
    // of the shared simulation revision, and the block depth also of every baked World.
    public static class WaterRules
    {
        // Water at least this deep at the feet slows the walk, about knee deep.
        public const float WadeDepth = 0.5f;

        public const float WadeSpeedMultiplier = 0.6f;

        // Deeper water stops movement at its edge, about chest deep.
        public const float BlockDepth = 1.2f;

        // A water surface this far above the feet still counts, which covers a character
        // that fell into deep water.
        public const float SurfaceSearchHeight = 4f;

        // Ground this close above the feet is the ground the character stands on.
        private const float StandingTolerance = 0.05f;

        private static readonly SimulationVector3 Down = new SimulationVector3(0f, -1f, 0f);

        // Measures how deep the water stands above the feet. Ground between the surface and
        // the feet, such as a lake bed above a tunnel, keeps the water out.
        public static bool TryGetDepth(
            ICollisionWorld collisionWorld,
            SimulationVector3 rootPosition,
            CollisionQueryBuffer queryBuffer,
            out float depth)
        {
            depth = 0f;
            var searchTop = new SimulationVector3(
                rootPosition.X,
                (float)(rootPosition.Y + SurfaceSearchHeight),
                rootPosition.Z);
            if (!CollisionRaycast.TryRaycast(
                    collisionWorld,
                    searchTop,
                    Down,
                    SurfaceSearchHeight,
                    CollisionLayers.Water,
                    queryBuffer,
                    out var surface))
            {
                return false;
            }

            var surfaceDepth = (float)(surface.Point.Y - rootPosition.Y);
            if (surfaceDepth <= 0f)
            {
                return false;
            }

            var openDistance = (float)(surfaceDepth - StandingTolerance);
            if (openDistance > 0f
                && CollisionRaycast.TryRaycast(
                    collisionWorld,
                    surface.Point,
                    Down,
                    openDistance,
                    CollisionLayers.CharacterMovement,
                    queryBuffer,
                    out _))
            {
                return false;
            }

            depth = surfaceDepth;
            return true;
        }

        // The walk speed multiplier for water of this depth at the feet.
        public static float SpeedMultiplier(float depth)
        {
            return depth >= WadeDepth ? WadeSpeedMultiplier : 1f;
        }
    }
}
