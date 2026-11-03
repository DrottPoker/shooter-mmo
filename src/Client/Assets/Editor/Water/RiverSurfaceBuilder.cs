using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The surface of a river: a ribbon along its center line, five vertices across, that reaches
    // under both banks and falls with the river, so the water runs fastest in the middle. Where
    // the river starts or ends in a lake or a sea, every row whose vertices lie on that water's
    // surface takes its level and stands still, and so does the next row, after which the flow
    // eases in over a few meters, so the surfaces meet without a seam. Where a waterfall falls
    // from the river's end, the last row lies on the lip exactly as wide as the river, and one row
    // past it keeps a little inside the falling water, whose flat top covers it; the water foams
    // where it tips over.
    internal static class RiverSurfaceBuilder
    {
        public const int Columns = 5;

        public static WaterSurfaceMesh Build(RiverModel river)
        {
            var rows = Rows(river);
            var shape = river.River;
            var transform = shape.transform;
            var surface = new WaterSurfaceMesh();
            foreach (var row in rows)
            {
                for (var column = 0; column < Columns; column++)
                {
                    var side = ((2f * column) / (Columns - 1)) - 1f;
                    var offset = row.Across * (side * row.HalfWidth);
                    var profile = 1f - (shape.EdgeSlowdown * side * side);
                    var world = new Vector3(row.Center.x + offset.x, row.Height, row.Center.y + offset.y);
                    surface.Add(
                        transform.InverseTransformPoint(world),
                        row.Direction * (row.Speed * profile),
                        row.Foam * profile,
                        0f,
                        0f);
                }
            }

            for (var row = 0; row < rows.Count - 1; row++)
            {
                for (var column = 0; column < Columns - 1; column++)
                {
                    var first = (row * Columns) + column;
                    surface.Triangle(first, first + 1, first + Columns + 1);
                    surface.Triangle(first, first + Columns + 1, first + Columns);
                }
            }

            return surface;
        }

        // The rows of the surface along the river in world space, from its start to its end.
        public static List<RiverRow> Rows(RiverModel river)
        {
            var path = river.Path;
            var shape = river.River;
            var length = path.Length;
            var steps = Mathf.Max(1, Mathf.CeilToInt(length / shape.RowSpacing));
            var rows = new List<RiverRow>(steps + 2);
            var fall = river.Fall;
            for (var step = 0; step <= steps; step++)
            {
                var along = length * step / steps;
                var values = path.ValuesAt(along);
                var reach = fall != null && step == steps ? 0f : shape.BankReach;
                rows.Add(new RiverRow(
                    along,
                    path.PlanarAt(along),
                    path.DirectionAt(along),
                    (values.Width * 0.5f) + reach,
                    values.Position.y,
                    values.Speed,
                    values.Foam));
            }

            if (fall != null)
            {
                var end = rows[rows.Count - 1];
                rows.Add(new RiverRow(
                    length + fall.Shape.PastLip,
                    end.Center + (end.Direction * fall.Shape.PastLip),
                    end.Direction,
                    end.HalfWidth - fall.Shape.InsideFall,
                    end.Height,
                    end.Speed,
                    end.Foam));
            }

            ApplyJunction(rows, river.Start, false);
            ApplyJunction(rows, river.End, true);
            if (fall != null)
            {
                for (var index = 0; index < rows.Count; index++)
                {
                    var row = rows[index];
                    var lip = fall.Shape.LipFoam * (1f - WorldBuilderGeometry.Smooth01((length - row.Along) / fall.Shape.LipFoamLength));
                    rows[index] = row.WithFoam(Mathf.Max(row.Foam, lip));
                }
            }

            return rows;
        }

        // Rows covered by the water the river meets take its level and stand still, and so does
        // the next one; beyond it the flow and the foam ease in over the junction length.
        private static void ApplyJunction(List<RiverRow> rows, RiverJunction junction, bool atEnd)
        {
            if (junction == null)
            {
                return;
            }

            var body = junction.Body;
            var still = -1;
            for (var step = 0; step < rows.Count; step++)
            {
                var index = atEnd ? rows.Count - 1 - step : step;
                if (!IsCovered(rows[index], body))
                {
                    break;
                }

                still = step;
            }

            if (still < 0)
            {
                return;
            }

            still = Mathf.Min(still + 1, rows.Count - 1);
            var edge = rows[atEnd ? rows.Count - 1 - still : still].Along;
            for (var step = 0; step < rows.Count; step++)
            {
                var index = atEnd ? rows.Count - 1 - step : step;
                var row = rows[index];
                if (step <= still)
                {
                    rows[index] = row.Still(body.Level);
                    continue;
                }

                var calm = WorldBuilderGeometry.Smooth01(Mathf.Abs(row.Along - edge) / RiverModel.JunctionLength);
                rows[index] = row.Calmed(calm);
            }
        }

        private static bool IsCovered(RiverRow row, OpenWaterModel body)
        {
            for (var column = 0; column < Columns; column++)
            {
                var side = ((2f * column) / (Columns - 1)) - 1f;
                if (body.Covers(row.Center + (row.Across * (side * row.HalfWidth))))
                {
                    return true;
                }
            }

            return false;
        }
    }

    // One row of a river's surface across its center line in world space.
    internal readonly struct RiverRow
    {
        public RiverRow(float along, Vector2 center, Vector2 direction, float halfWidth, float height, float speed, float foam)
        {
            Along = along;
            Center = center;
            Direction = direction;
            HalfWidth = halfWidth;
            Height = height;
            Speed = speed;
            Foam = foam;
        }

        public float Along { get; }

        public Vector2 Center { get; }

        public Vector2 Direction { get; }

        // Across the river, from its right bank to its left seen downstream.
        public Vector2 Across
        {
            get { return new Vector2(-Direction.y, Direction.x); }
        }

        public float HalfWidth { get; }

        public float Height { get; }

        public float Speed { get; }

        public float Foam { get; }

        public RiverRow Still(float level)
        {
            return new RiverRow(Along, Center, Direction, HalfWidth, level, 0f, 0f);
        }

        public RiverRow Calmed(float amount)
        {
            return new RiverRow(Along, Center, Direction, HalfWidth, Height, Speed * amount, Foam * amount);
        }

        public RiverRow WithFoam(float foam)
        {
            return new RiverRow(Along, Center, Direction, HalfWidth, Height, Speed, foam);
        }
    }
}
