using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The surface of a lake: a grid of cells in the lake's space over every cell whose center lies
    // inside the shore or within the bank reach of it, so the jagged rim of the grid lies under the
    // bank. The water is still, apart from where a waterfall lands: there it churns and foams all
    // over and spreads out from the landing.
    internal static class LakeSurfaceBuilder
    {
        public static WaterSurfaceMesh Build(LakeModel lake)
        {
            var shape = lake.Lake;
            var shore = shape.Shore;
            var surface = new WaterSurfaceMesh();
            if (shore.Count < 3)
            {
                return surface;
            }

            var cell = shape.CellSize;
            var reach = shape.BankReach;
            var bounds = WorldBuilderGeometry.Bounds(shore);
            var firstX = Mathf.FloorToInt((bounds.xMin - reach) / cell);
            var lastX = Mathf.CeilToInt((bounds.xMax + reach) / cell);
            var firstZ = Mathf.FloorToInt((bounds.yMin - reach) / cell);
            var lastZ = Mathf.CeilToInt((bounds.yMax + reach) / cell);
            var corners = new Dictionary<long, int>();
            for (var cellZ = firstZ; cellZ < lastZ; cellZ++)
            {
                for (var cellX = firstX; cellX < lastX; cellX++)
                {
                    var center = new Vector2((cellX + 0.5f) * cell, (cellZ + 0.5f) * cell);
                    if (!WorldBuilderGeometry.Contains(shore, center)
                        && WorldBuilderGeometry.DistanceToOutline(shore, center) > reach)
                    {
                        continue;
                    }

                    var a = Corner(surface, corners, lake, cellX, cellZ, cell);
                    var b = Corner(surface, corners, lake, cellX + 1, cellZ, cell);
                    var c = Corner(surface, corners, lake, cellX + 1, cellZ + 1, cell);
                    var d = Corner(surface, corners, lake, cellX, cellZ + 1, cell);
                    surface.Triangle(a, d, c);
                    surface.Triangle(a, c, b);
                }
            }

            return surface;
        }

        private static int Corner(
            WaterSurfaceMesh surface,
            Dictionary<long, int> corners,
            LakeModel lake,
            int cornerX,
            int cornerZ,
            float cell)
        {
            var key = ((long)cornerX << 32) | (uint)cornerZ;
            if (corners.TryGetValue(key, out var index))
            {
                return index;
            }

            var local = new Vector3(cornerX * cell, 0f, cornerZ * cell);
            var world = lake.Shape.transform.TransformPoint(local);
            WaterfallChurn.At(lake, new Vector2(world.x, world.z), out var flow, out var churn);
            index = surface.Add(local, flow, churn, 0f, 0f);
            corners.Add(key, index);
            return index;
        }
    }

    // Where waterfalls land in a water body, the water churns within a radius of the landing,
    // fades out beyond it, and spreads out from the landing.
    internal static class WaterfallChurn
    {
        public static void At(SurfaceModel body, Vector2 point, out Vector2 flow, out float churn)
        {
            flow = Vector2.zero;
            churn = 0f;
            foreach (var waterfall in body.Landings)
            {
                var shape = waterfall.Shape;
                var fromLanding = point - waterfall.Fall.Landing;
                var distance = fromLanding.magnitude;
                var amount = 1f - WorldBuilderGeometry.Smooth01((distance - shape.ChurnRadius) / shape.ChurnFade);
                churn = Mathf.Max(churn, amount);
                if (distance > 0.01f)
                {
                    flow += fromLanding * (shape.SpreadSpeed * amount * WorldBuilderGeometry.Smooth01(distance) / distance);
                }
            }
        }
    }
}
