using System.Collections.Generic;
using Mmorpg.Client.Worlds;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The surface of a sea: a grid of cells in the sea's space over every cell whose center lies
    // inside its outline or within the bank reach of it, whose vertices the waves lift, with foam
    // lines that roll onto its beaches. The waves calm down toward the sides of the World where
    // the sea runs on to the horizon, and the waves and the foam lines both calm down at a river's
    // mouth, so the surfaces meet without a seam. Beyond the World's ground on those sides, a flat
    // and still surface over a seabed continues the sea to the horizon; it only shows.
    internal static class SeaSurfaceBuilder
    {
        // The river's water calms the sea within its half width and this much more of its end.
        private const float MouthMargin = 0.5f;

        public static WaterSurfaceMesh Build(SeaModel sea, Rect? worldEdge)
        {
            var shape = sea.Sea;
            var outline = shape.Outline;
            var surface = new WaterSurfaceMesh();
            if (outline.Count < 3)
            {
                return surface;
            }

            var cell = shape.CellSize;
            var reach = shape.BankReach;
            var bounds = WorldBuilderGeometry.Bounds(outline);
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
                    if ((!WorldBuilderGeometry.Contains(outline, center)
                            && WorldBuilderGeometry.DistanceToOutline(outline, center) > reach)
                        || IsBeyondHorizon(shape, worldEdge, center))
                    {
                        continue;
                    }

                    var a = Corner(surface, corners, sea, worldEdge, cellX, cellZ, cell);
                    var b = Corner(surface, corners, sea, worldEdge, cellX + 1, cellZ, cell);
                    var c = Corner(surface, corners, sea, worldEdge, cellX + 1, cellZ + 1, cell);
                    var d = Corner(surface, corners, sea, worldEdge, cellX, cellZ + 1, cell);
                    surface.Triangle(a, d, c);
                    surface.Triangle(a, c, b);
                }
            }

            return surface;
        }

        // The still sea beyond the World's ground: one strip beyond each horizon side, the west
        // and east strips also across the corners, in the sea's space at its level.
        public static WaterSurfaceMesh BuildHorizon(SeaModel sea, Rect worldEdge)
        {
            var surface = new WaterSurfaceMesh();
            foreach (var strip in HorizonStrips(sea.Sea, worldEdge))
            {
                var transform = sea.Shape.transform;
                var first = surface.Add(Local(transform, strip.xMin, sea.Level, strip.yMin), Vector2.zero, 0f, 0f, 1f);
                surface.Add(Local(transform, strip.xMin, sea.Level, strip.yMax), Vector2.zero, 0f, 0f, 1f);
                surface.Add(Local(transform, strip.xMax, sea.Level, strip.yMax), Vector2.zero, 0f, 0f, 1f);
                surface.Add(Local(transform, strip.xMax, sea.Level, strip.yMin), Vector2.zero, 0f, 0f, 1f);
                surface.Triangle(first, first + 1, first + 2);
                surface.Triangle(first, first + 2, first + 3);
            }

            return surface;
        }

        // The seabed below the still sea beyond the World's ground, facing up.
        public static void BuildSeabed(SeaModel sea, Rect worldEdge, Mesh mesh)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var height = sea.Level - sea.Sea.SeabedDepth;
            foreach (var strip in HorizonStrips(sea.Sea, worldEdge))
            {
                var transform = sea.Shape.transform;
                var first = vertices.Count;
                vertices.Add(Local(transform, strip.xMin, height, strip.yMin));
                vertices.Add(Local(transform, strip.xMin, height, strip.yMax));
                vertices.Add(Local(transform, strip.xMax, height, strip.yMax));
                vertices.Add(Local(transform, strip.xMax, height, strip.yMin));
                triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }

            mesh.Clear(false);
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        public static IEnumerable<Rect> HorizonStrips(SeaShape sea, Rect worldEdge)
        {
            var extent = sea.HorizonExtent;
            var horizon = sea.Horizon;
            if (extent <= 0f)
            {
                yield break;
            }

            var south = (horizon & HorizonSides.South) != 0 ? extent : 0f;
            var north = (horizon & HorizonSides.North) != 0 ? extent : 0f;
            if ((horizon & HorizonSides.West) != 0)
            {
                yield return Rect.MinMaxRect(worldEdge.xMin - extent, worldEdge.yMin - south, worldEdge.xMin, worldEdge.yMax + north);
            }

            if ((horizon & HorizonSides.East) != 0)
            {
                yield return Rect.MinMaxRect(worldEdge.xMax, worldEdge.yMin - south, worldEdge.xMax + extent, worldEdge.yMax + north);
            }

            if (south > 0f)
            {
                yield return Rect.MinMaxRect(worldEdge.xMin, worldEdge.yMin - extent, worldEdge.xMax, worldEdge.yMin);
            }

            if (north > 0f)
            {
                yield return Rect.MinMaxRect(worldEdge.xMin, worldEdge.yMax, worldEdge.xMax, worldEdge.yMax + extent);
            }
        }

        private static int Corner(
            WaterSurfaceMesh surface,
            Dictionary<long, int> corners,
            SeaModel sea,
            Rect? worldEdge,
            int cornerX,
            int cornerZ,
            float cell)
        {
            var key = ((long)cornerX << 32) | (uint)cornerZ;
            if (corners.TryGetValue(key, out var index))
            {
                return index;
            }

            var shape = sea.Sea;
            var local = new Vector3(cornerX * cell, 0f, cornerZ * cell);
            var world = shape.transform.TransformPoint(local);
            var point = new Vector2(world.x, world.z);
            var mouths = MouthCalm(sea, point);
            var waves = shape.Waves * HorizonCalm(shape, worldEdge, point) * mouths;
            WaterfallChurn.At(sea, point, out var flow, out var churn);
            index = surface.Add(local, flow, churn, waves, mouths);
            corners.Add(key, index);
            return index;
        }

        // Beyond the World's ground on a horizon side the still sea continues this one, so this
        // surface, which collides, stops at the edge.
        private static bool IsBeyondHorizon(SeaShape sea, Rect? worldEdge, Vector2 localCenter)
        {
            if (!worldEdge.HasValue || sea.Horizon == HorizonSides.None)
            {
                return false;
            }

            var world = sea.transform.TransformPoint(new Vector3(localCenter.x, 0f, localCenter.y));
            var edge = worldEdge.Value;
            return ((sea.Horizon & HorizonSides.West) != 0 && world.x < edge.xMin)
                || ((sea.Horizon & HorizonSides.East) != 0 && world.x > edge.xMax)
                || ((sea.Horizon & HorizonSides.South) != 0 && world.z < edge.yMin)
                || ((sea.Horizon & HorizonSides.North) != 0 && world.z > edge.yMax);
        }

        // 0 at the World's edge on a horizon side, rising to 1 the calm distance inside it.
        private static float HorizonCalm(SeaShape sea, Rect? worldEdge, Vector2 point)
        {
            if (!worldEdge.HasValue || sea.Horizon == HorizonSides.None)
            {
                return 1f;
            }

            var edge = worldEdge.Value;
            var nearest = float.MaxValue;
            if ((sea.Horizon & HorizonSides.West) != 0)
            {
                nearest = Mathf.Min(nearest, point.x - edge.xMin);
            }

            if ((sea.Horizon & HorizonSides.East) != 0)
            {
                nearest = Mathf.Min(nearest, edge.xMax - point.x);
            }

            if ((sea.Horizon & HorizonSides.South) != 0)
            {
                nearest = Mathf.Min(nearest, point.y - edge.yMin);
            }

            if ((sea.Horizon & HorizonSides.North) != 0)
            {
                nearest = Mathf.Min(nearest, edge.yMax - point.y);
            }

            return Mathf.Clamp01(nearest / sea.CalmDistance);
        }

        // 0 on the water of a river that flows into the sea, rising to 1 the calm distance beyond
        // its surface.
        private static float MouthCalm(SeaModel sea, Vector2 point)
        {
            var calm = 1f;
            foreach (var river in sea.Mouths)
            {
                var path = river.Path;
                var distance = path.DistanceTo(point, out var along);
                var reach = (path.ValuesAt(along).Width * 0.5f) + river.Shape.BankReach + MouthMargin;
                calm *= WorldBuilderGeometry.Smooth01((distance - reach) / sea.Sea.CalmDistance);
            }

            return calm;
        }

        private static Vector3 Local(Transform transform, float x, float y, float z)
        {
            return transform.InverseTransformPoint(new Vector3(x, y, z));
        }
    }
}
