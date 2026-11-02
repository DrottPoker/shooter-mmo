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

        // A surface point is tested this far above itself, so ground at its exact height counts
        // as below it.
        private const float OpenSurfaceOffset = 0.001f;
        private const float TerrainProbeHeight = 1024f;

        private static readonly SimulationVector3 Down = new SimulationVector3(0f, -1f, 0f);

        // Water whose surface lies below terrain or inside a box is dry land, such as the part
        // of a lake's surface drawn under its bank. The deep water edges and the depth query
        // share this test, so they agree on where the water is.
        public static bool IsOpenSurface(
            ICollisionWorld collisionWorld,
            SimulationVector3 surfacePoint,
            CollisionQueryBuffer queryBuffer)
        {
            var point = new SimulationVector3(
                surfacePoint.X,
                (float)(surfacePoint.Y + OpenSurfaceOffset),
                surfacePoint.Z);
            if (CollisionRaycast.TryRaycast(
                    collisionWorld,
                    new SimulationVector3(point.X, (float)(point.Y + TerrainProbeHeight), point.Z),
                    Down,
                    TerrainProbeHeight,
                    CollisionLayers.Terrain,
                    queryBuffer,
                    out _))
            {
                return false;
            }

            queryBuffer.Clear();
            collisionWorld.QueryShapes(new CollisionAabb(point, point), CollisionLayers.CharacterMovement, queryBuffer);
            for (var index = 0; index < queryBuffer.Shapes.Count; index++)
            {
                if (queryBuffer.Shapes[index] is CollisionBox box)
                {
                    var local = box.Rotation.InverseRotate(point - box.Center);
                    if (System.Math.Abs(local.X) < box.HalfExtents.X
                        && System.Math.Abs(local.Y) < box.HalfExtents.Y
                        && System.Math.Abs(local.Z) < box.HalfExtents.Z)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // Measures how deep the water stands above the feet. Water drawn under a bank is dry,
        // and ground between the surface and the feet, such as a lake bed above a tunnel,
        // keeps the water out.
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
            if (surfaceDepth <= 0f || !IsOpenSurface(collisionWorld, surface.Point, queryBuffer))
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
