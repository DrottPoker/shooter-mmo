using System.Collections.Generic;
using Mmorpg.Client.Worlds;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The surface of a sea: a grid of cells in the sea's space over every cell whose center lies
    // inside its outline or within the bank reach of it, whose vertices the waves lift, with foam
    // lines that roll onto its beaches. The waves calm down toward the sides of the World where
    // the sea runs on to the horizon, and the waves and the foam lines both calm down where a
    // river starts or ends in the sea, so the surfaces meet without a seam. Beyond the World's
    // ground on those sides, a flat and still surface over a seabed continues the sea to the
    // horizon; it only shows. A cell that the World's edge crosses there is cut off at the edge,
    // so the two meet exactly however the sea's grid lies.
    internal static class SeaSurfaceBuilder
    {
        // A river's water calms the sea within its half width and this much more of its end.
        private const float RiverMargin = 0.5f;

        // A cut cell keeps a part at least this share of a whole cell.
        private const float SmallestPart = 0.0001f;

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
                    if (!WorldBuilderGeometry.Contains(outline, center)
                        && WorldBuilderGeometry.DistanceToOutline(outline, center) > reach)
                    {
                        continue;
                    }

                    if (!CrossesHorizon(shape, worldEdge, cellX, cellZ, cell))
                    {
                        var a = Corner(surface, corners, sea, worldEdge, cellX, cellZ, cell);
                        var b = Corner(surface, corners, sea, worldEdge, cellX + 1, cellZ, cell);
                        var c = Corner(surface, corners, sea, worldEdge, cellX + 1, cellZ + 1, cell);
                        var d = Corner(surface, corners, sea, worldEdge, cellX, cellZ + 1, cell);
                        surface.Triangle(a, d, c);
                        surface.Triangle(a, c, b);
                        continue;
                    }

                    AddCutCell(surface, corners, sea, worldEdge.Value, cellX, cellZ, cell);
                }
            }

            return surface;
        }

        // The part of a cell inside the World's edge on the horizon sides, as a fan of triangles
        // that shares the cell's corners inside with its neighbors.
        private static void AddCutCell(
            WaterSurfaceMesh surface,
            Dictionary<long, int> corners,
            SeaModel sea,
            Rect worldEdge,
            int cellX,
            int cellZ,
            float cell)
        {
            var shape = sea.Sea;
            var gridCorners = new[]
            {
                new Vector2Int(cellX, cellZ),
                new Vector2Int(cellX + 1, cellZ),
                new Vector2Int(cellX + 1, cellZ + 1),
                new Vector2Int(cellX, cellZ + 1)
            };
            var polygon = new List<Vector2>();
            foreach (var corner in gridCorners)
            {
                polygon.Add(WorldPoint(shape, corner, cell));
            }

            foreach (var side in new[] { HorizonSides.West, HorizonSides.East, HorizonSides.South, HorizonSides.North })
            {
                if ((shape.Horizon & side) != 0)
                {
                    polygon = Clip(polygon, side, worldEdge);
                }
            }

            // A corner on the edge comes out twice, once as a corner and once as a cut, which
            // would make a triangle without area.
            var closest = SmallestPart * SmallestPart * cell * cell;
            var distinct = new List<Vector2>();
            foreach (var point in polygon)
            {
                if (distinct.Count == 0 || (point - distinct[distinct.Count - 1]).sqrMagnitude >= closest)
                {
                    distinct.Add(point);
                }
            }

            if (distinct.Count > 1 && (distinct[0] - distinct[distinct.Count - 1]).sqrMagnitude < closest)
            {
                distinct.RemoveAt(distinct.Count - 1);
            }

            polygon = distinct;
            if (polygon.Count < 3 || Mathf.Abs(Area(polygon)) < SmallestPart * cell * cell)
            {
                return;
            }

            var indices = new List<int>(polygon.Count);
            foreach (var point in polygon)
            {
                var index = -1;
                foreach (var corner in gridCorners)
                {
                    if (WorldPoint(shape, corner, cell) == point)
                    {
                        index = Corner(surface, corners, sea, worldEdge, corner.x, corner.y, cell);
                        break;
                    }
                }

                indices.Add(index >= 0 ? index : AddVertex(surface, sea, worldEdge, point));
            }

            for (var index = 1; index < indices.Count - 1; index++)
            {
                surface.Triangle(indices[0], indices[index], indices[index + 1]);
            }
        }

        // The part of a convex polygon inside the World's edge on one side, by cutting each edge
        // that crosses it, where the cut lies exactly on the World's edge.
        private static List<Vector2> Clip(List<Vector2> polygon, HorizonSides side, Rect worldEdge)
        {
            var clipped = new List<Vector2>();
            for (var index = 0; index < polygon.Count; index++)
            {
                var from = polygon[index];
                var to = polygon[(index + 1) % polygon.Count];
                var fromInside = InsideOf(side, worldEdge, from);
                var toInside = InsideOf(side, worldEdge, to);
                if (fromInside >= 0f)
                {
                    clipped.Add(from);
                }

                if ((fromInside >= 0f) != (toInside >= 0f))
                {
                    var cut = Vector2.Lerp(from, to, fromInside / (fromInside - toInside));
                    clipped.Add(side == HorizonSides.West ? new Vector2(worldEdge.xMin, cut.y)
                        : side == HorizonSides.East ? new Vector2(worldEdge.xMax, cut.y)
                        : side == HorizonSides.South ? new Vector2(cut.x, worldEdge.yMin)
                        : new Vector2(cut.x, worldEdge.yMax));
                }
            }

            return clipped;
        }

        private static float Area(List<Vector2> polygon)
        {
            var twice = 0f;
            for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
            {
                twice += (polygon[previous].x * polygon[index].y) - (polygon[index].x * polygon[previous].y);
            }

            return twice * 0.5f;
        }

        // How far inside the World's edge on one side a point lies, negative beyond it.
        private static float InsideOf(HorizonSides side, Rect edge, Vector2 point)
        {
            switch (side)
            {
                case HorizonSides.West:
                    return point.x - edge.xMin;
                case HorizonSides.East:
                    return edge.xMax - point.x;
                case HorizonSides.South:
                    return point.y - edge.yMin;
                default:
                    return edge.yMax - point.y;
            }
        }

        // Whether a corner of the cell lies beyond the World's edge on a horizon side.
        private static bool CrossesHorizon(SeaShape sea, Rect? worldEdge, int cellX, int cellZ, float cell)
        {
            if (!worldEdge.HasValue || sea.Horizon == HorizonSides.None)
            {
                return false;
            }

            for (var cornerZ = cellZ; cornerZ <= cellZ + 1; cornerZ++)
            {
                for (var cornerX = cellX; cornerX <= cellX + 1; cornerX++)
                {
                    var point = WorldPoint(sea, new Vector2Int(cornerX, cornerZ), cell);
                    foreach (var side in new[] { HorizonSides.West, HorizonSides.East, HorizonSides.South, HorizonSides.North })
                    {
                        if ((sea.Horizon & side) != 0 && InsideOf(side, worldEdge.Value, point) < 0f)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static Vector2 WorldPoint(SeaShape sea, Vector2Int corner, float cell)
        {
            var world = sea.transform.TransformPoint(new Vector3(corner.x * cell, 0f, corner.y * cell));
            return new Vector2(world.x, world.z);
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

            var local = new Vector3(cornerX * cell, 0f, cornerZ * cell);
            var world = sea.Sea.transform.TransformPoint(local);
            index = AddAt(surface, sea, worldEdge, local, new Vector2(world.x, world.z));
            corners.Add(key, index);
            return index;
        }

        // A vertex where the World's edge cuts a cell, on that edge, where the waves are calm.
        private static int AddVertex(WaterSurfaceMesh surface, SeaModel sea, Rect worldEdge, Vector2 point)
        {
            var local = sea.Shape.transform.InverseTransformPoint(new Vector3(point.x, sea.Level, point.y));
            local.y = 0f;
            return AddAt(surface, sea, worldEdge, local, point);
        }

        // A vertex in the sea's space, with what the water shader reads there from the point in
        // world X and Z.
        private static int AddAt(WaterSurfaceMesh surface, SeaModel sea, Rect? worldEdge, Vector3 local, Vector2 point)
        {
            var shape = sea.Sea;
            var rivers = RiverCalm(sea, point);
            var waves = shape.Waves * HorizonCalm(shape, worldEdge, point) * rivers;
            WaterfallChurn.At(sea, point, out var flow, out var churn);
            return surface.Add(local, flow, churn, waves, rivers);
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

        // 0 on the water of a river that starts or ends in the sea, rising to 1 the calm distance
        // beyond its surface. The sea's triangles blend between their vertices, so every vertex
        // of a triangle that reaches the river's surface stays calm: the calm reaches a cell's
        // diagonal further.
        private static float RiverCalm(SeaModel sea, Vector2 point)
        {
            var calm = 1f;
            var cellDiagonal = sea.Sea.CellSize * 1.4143f;
            foreach (var river in sea.Rivers)
            {
                var path = river.Path;
                var distance = path.DistanceTo(point, out var along);
                var reach = (path.ValuesAt(along).Width * 0.5f) + river.Shape.BankReach + RiverMargin + cellDiagonal;
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
