using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Where a waterfall falls: from the lip of a river, as wide and as deep as the river is
    // there, along the path the water takes when it leaves the river's surface at the
    // waterfall's speed, down into the water below.
    internal readonly struct WaterfallFall
    {
        public const float Gravity = 9.81f;

        public WaterfallFall(
            Vector2 lip,
            Vector2 downstream,
            float halfWidth,
            float surface,
            float depth,
            float landingLevel,
            float speed)
        {
            Lip = lip;
            Downstream = downstream.normalized;
            HalfWidth = halfWidth;
            Surface = surface;
            Depth = depth;
            LandingLevel = landingLevel;
            Speed = speed;
        }

        public Vector2 Lip { get; }

        public Vector2 Downstream { get; }

        public float HalfWidth { get; }

        public float Surface { get; }

        public float Depth { get; }

        public float LandingLevel { get; }

        public float Speed { get; }

        // Across the fall, so the column's around coordinate runs from its back through its front.
        public Vector2 Across
        {
            get { return new Vector2(Downstream.y, -Downstream.x); }
        }

        // How long the water's surface falls from the lip to the water below.
        public float Seconds
        {
            get { return Mathf.Sqrt(2f * Mathf.Max(0f, Surface - LandingLevel) / Gravity); }
        }

        // Where the water lands on the water below.
        public Vector2 Landing
        {
            get { return Lip + (Downstream * (Speed * Seconds)); }
        }
    }

    // The falling water as a body of its own: a column whose cross section is a rounded slab, as
    // wide as the river at the lip and as thick as its water, that follows the water's path and
    // grows wider, thicker, and rounder as it falls and breaks up, down to the water below. Up to
    // where the river's surface ends, just past the lip, it runs straight along the river with
    // its top in that surface and fades in over it, so the river turns into the fall without a
    // line. Its outside faces outward, so it shows its body from every side. The texture
    // coordinate holds the meters around the column from its front, the seconds the water has
    // fallen, negative before it falls, how far down the fall the vertex lies, and how far the
    // column has faded in; the tangents run around the column.
    internal static class WaterfallColumn
    {
        public const int Rows = 24;
        public const int Around = 64;

        public static void Build(
            WaterfallFall fall,
            float approach,
            float pastLip,
            float spreadWidth,
            float bottomHalfThickness,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector4> tangents,
            List<Vector4> column,
            List<int> triangles)
        {
            var top = fall.Surface - (fall.Depth * 0.5f);
            var seconds = Mathf.Sqrt(2f * Mathf.Max(0f, top - (fall.LandingLevel - 0.05f)) / WaterfallFall.Gravity);
            var across3 = new Vector3(fall.Across.x, 0f, fall.Across.y);
            var downstream3 = new Vector3(fall.Downstream.x, 0f, fall.Downstream.y);
            var section = new Vector3[Around + 1];
            var sectionNormals = new Vector3[Around + 1];

            // Row -1 starts the approach up the river; row 0 lies where the river's surface ends
            // and the water starts to fall.
            for (var row = -1; row <= Rows; row++)
            {
                var amount = Mathf.Max(0f, (float)row / Rows);
                var time = row < 0 ? -(approach + pastLip) / fall.Speed : seconds * amount;
                var falling = Mathf.Max(0f, time);
                var flat = fall.Lip + (fall.Downstream * (pastLip + (fall.Speed * time)));
                var center = new Vector3(flat.x, top - (0.5f * WaterfallFall.Gravity * falling * falling), flat.y);

                // The section lies square to the water's path: across the river, and along the
                // normal, which faces up at the lip and downstream once the water falls.
                var velocity = (downstream3 * fall.Speed) + (Vector3.down * (WaterfallFall.Gravity * falling));
                var normal = Vector3.Cross(across3, velocity).normalized;
                if (Vector3.Dot(normal, downstream3 + Vector3.up) < 0f)
                {
                    normal = -normal;
                }

                // A slab as wide as the river it leaves and square enough that its flat top
                // covers the river's surface past the lip, that rounds evenly into a
                // superellipse of exponent 4 as it falls.
                var spread = Smooth01(amount * 1.3f);
                var halfWidth = Mathf.Lerp(fall.HalfWidth, fall.HalfWidth + spreadWidth, spread);
                var halfThickness = Mathf.Lerp(fall.Depth * 0.5f, bottomHalfThickness, spread);
                var roundness = Mathf.Lerp(1f / 30f, 0.5f, spread);
                var exponent = 2f / roundness;
                for (var index = 0; index <= Around; index++)
                {
                    // From the back around through the front. The quarter turns get exact zeros,
                    // which a small roundness would otherwise raise from rounding errors to
                    // large offsets.
                    var angle = (-0.5f * Mathf.PI) + ((2f * Mathf.PI * index) / Around);
                    var cos = index % (Around / 2) == 0 ? 0f : Mathf.Cos(angle);
                    var sin = index % (Around / 2) == Around / 4 ? 0f : Mathf.Sin(angle);
                    var x = halfWidth * Mathf.Sign(cos) * Mathf.Pow(Mathf.Abs(cos), roundness);
                    var y = halfThickness * Mathf.Sign(sin) * Mathf.Pow(Mathf.Abs(sin), roundness);
                    section[index] = center + (across3 * x) + (normal * y);
                    var gradientX = Mathf.Sign(x) * Mathf.Pow(Mathf.Abs(x) / halfWidth, exponent - 1f) / halfWidth;
                    var gradientY = Mathf.Sign(y) * Mathf.Pow(Mathf.Abs(y) / halfThickness, exponent - 1f) / halfThickness;
                    sectionNormals[index] = ((across3 * gradientX) + (normal * gradientY)).normalized;
                }

                // Meters around the section, counted from its front, so the seam lies at the back.
                var lengths = new float[Around + 1];
                for (var index = 1; index <= Around; index++)
                {
                    lengths[index] = lengths[index - 1] + Vector3.Distance(section[index - 1], section[index]);
                }

                var front = lengths[Around / 2];
                for (var index = 0; index <= Around; index++)
                {
                    var next = section[Mathf.Min(index + 1, Around)] - section[Mathf.Max(index - 1, 0)];
                    vertices.Add(section[index]);
                    normals.Add(sectionNormals[index]);
                    tangents.Add(new Vector4(next.normalized.x, next.normalized.y, next.normalized.z, 1f));
                    column.Add(new Vector4(lengths[index] - front, time, amount, row < 0 ? 0f : 1f));
                }
            }

            // The rows from -1 to the last give rows + 2 rings to join.
            var ring = Around + 1;
            for (var row = 0; row <= Rows; row++)
            {
                for (var index = 0; index < Around; index++)
                {
                    var a = (row * ring) + index;
                    var b = a + ring;
                    var c = b + 1;
                    var d = a + 1;

                    // Front faces wind so their edges' cross product points out of the column.
                    var outward = normals[a] + normals[c];
                    var facesOut = Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), outward) >= 0f;
                    triangles.AddRange(facesOut ? new[] { a, b, c, a, c, d } : new[] { a, c, b, a, d, c });
                }
            }
        }

        private static float Smooth01(float value)
        {
            var clamped = Mathf.Clamp01(value);
            return clamped * clamped * (3f - (2f * clamped));
        }
    }
}
