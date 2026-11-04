using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The rules every water body of a World follows, so it draws, bakes, and meets other water as
    // it should: a valid and unique body id, a surface mesh with the data the water shader reads,
    // a material with the water shader, no collider, no static batching, no occluding, and where
    // two bodies overlap at about the same height, surfaces that agree: the same level, still
    // water, and no foam, waves, or foam lines. See docs/DEVELOPMENT_WORLDS.md under Water
    // Authoring.
    internal static class WaterValidation
    {
        // Where two surfaces lie this close in height, they meet and must agree.
        public const float MeetingHeight = 0.25f;

        public const float LevelTolerance = 0.005f;
        public const float FlowTolerance = 0.02f;
        public const float ColorTolerance = 0.02f;

        public static List<WorldBuilderIssue> Validate(IReadOnlyList<WaterBody> bodies)
        {
            var issues = new List<WorldBuilderIssue>();
            foreach (var duplicate in bodies.GroupBy(body => body.BodyId).Where(group => group.Count() > 1))
            {
                foreach (var body in duplicate)
                {
                    issues.Add(new WorldBuilderIssue(body, "The body id '" + duplicate.Key + "' is used by more than one water body."));
                }
            }

            var surfaces = new List<SampledSurface>();
            foreach (var body in bodies)
            {
                if (ValidateBody(body, issues))
                {
                    surfaces.Add(new SampledSurface(body));
                }
            }

            for (var first = 0; first < surfaces.Count; first++)
            {
                for (var second = first + 1; second < surfaces.Count; second++)
                {
                    ValidateJunction(surfaces[first], surfaces[second], issues);
                    ValidateJunction(surfaces[second], surfaces[first], issues);
                }
            }

            return issues;
        }

        // Returns whether the body's surface can be sampled for its junctions.
        private static bool ValidateBody(WaterBody body, List<WorldBuilderIssue> issues)
        {
            if (!body.TryValidate(out var error))
            {
                issues.Add(new WorldBuilderIssue(body, error));
                return false;
            }

            if (body.GetComponents<Collider>().Length > 0)
            {
                issues.Add(new WorldBuilderIssue(body, "A water body has no collider; the bake reads its surface mesh."));
            }

            if (GameObjectUtility.AreStaticEditorFlagsSet(body.gameObject, StaticEditorFlags.BatchingStatic))
            {
                issues.Add(new WorldBuilderIssue(
                    body,
                    "A water body must not be Batching Static: static batching merges its surface with others, "
                        + "and the client reads each surface to put wakes on its waves."));
            }

            if (GameObjectUtility.AreStaticEditorFlagsSet(body.gameObject, StaticEditorFlags.OccluderStatic))
            {
                issues.Add(new WorldBuilderIssue(
                    body,
                    "A water body should not be Occluder Static: water is seen through, so once occlusion culling is "
                        + "baked it would hide the ground below it and what lies behind it.",
                    WorldBuilderIssueSeverity.Warning));
            }

            var renderer = body.GetComponent<MeshRenderer>();
            var material = renderer != null ? renderer.sharedMaterial : null;
            if (material == null || material.shader == null || material.shader.name != WaterAssets.ShaderName)
            {
                issues.Add(new WorldBuilderIssue(body, "A water body draws with M_Water or a variant of it, the " + WaterAssets.ShaderName + " shader."));
            }

            var mesh = body.Surface;
            if (!mesh.isReadable)
            {
                issues.Add(new WorldBuilderIssue(body, "The surface mesh cannot be read; the bake and the client read it, so turn on Read/Write for it."));
                return false;
            }

            if (mesh.vertexCount == 0)
            {
                issues.Add(new WorldBuilderIssue(body, "The water has no surface: its mesh is empty."));
                return false;
            }

            var flows = new List<Vector2>();
            mesh.GetUVs(1, flows);
            if (mesh.colors.Length != mesh.vertexCount || flows.Count != mesh.vertexCount)
            {
                issues.Add(new WorldBuilderIssue(
                    body,
                    "The surface mesh lacks the flow or the vertex colors the water shader reads; without them "
                        + "it foams and waves all over. Make it with a World Builder shape."));
                return false;
            }

            return true;
        }

        // Every vertex of the first surface that lies on the second at about its height agrees
        // with the second there.
        private static void ValidateJunction(SampledSurface first, SampledSurface second, List<WorldBuilderIssue> issues)
        {
            if (!first.Bounds.Intersects(second.Bounds))
            {
                return;
            }

            for (var index = 0; index < first.Vertices.Length; index++)
            {
                var vertex = first.Vertices[index];
                if (!second.TrySample(vertex.x, vertex.z, out var height, out var flow, out var color)
                    || Mathf.Abs(height - vertex.y) > MeetingHeight)
                {
                    continue;
                }

                var problem = Mathf.Abs(height - vertex.y) > LevelTolerance ? "lie at different levels"
                    : first.Flows[index].magnitude > FlowTolerance || flow.magnitude > FlowTolerance ? "flow where they overlap"
                    : Mathf.Max(MaximumOf(first.Colors[index]), MaximumOf(color)) > ColorTolerance ? "foam or wave where they overlap"
                    : null;
                if (problem != null)
                {
                    issues.Add(new WorldBuilderIssue(
                        first.Body,
                        "'" + first.Body.BodyId + "' and '" + second.Body.BodyId + "' " + problem + " at ("
                            + vertex.x.ToString("0.0") + ", " + vertex.z.ToString("0.0")
                            + "), so they meet with a seam. Where water bodies meet, their surfaces lie at the same "
                            + "level and stand still, without foam, waves, or foam lines."));
                    return;
                }
            }
        }

        private static float MaximumOf(Color color)
        {
            return Mathf.Max(color.r, Mathf.Max(color.g, color.b));
        }

        // A water body's surface in world space with what the water shader reads at each vertex,
        // its triangles sorted into a grid in X and Z for fast lookups.
        private sealed class SampledSurface
        {
            private const float CellSize = 4f;

            private readonly int[] triangles;
            private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();

            public SampledSurface(WaterBody body)
            {
                Body = body;
                var mesh = body.Surface;
                var localToWorld = body.transform.localToWorldMatrix;
                Vertices = mesh.vertices.Select(vertex => localToWorld.MultiplyPoint3x4(vertex)).ToArray();
                var flows = new List<Vector2>();
                mesh.GetUVs(1, flows);
                Flows = flows.ToArray();
                Colors = mesh.colors;
                triangles = mesh.triangles;
                var bounds = GeometryUtility.CalculateBounds(Vertices, Matrix4x4.identity);
                bounds.Expand(new Vector3(0.01f, MeetingHeight * 2f, 0.01f));
                Bounds = bounds;
                for (var triangle = 0; triangle < triangles.Length; triangle += 3)
                {
                    var a = Vertices[triangles[triangle]];
                    var b = Vertices[triangles[triangle + 1]];
                    var c = Vertices[triangles[triangle + 2]];
                    for (var cellZ = Cell(Mathf.Min(a.z, Mathf.Min(b.z, c.z))); cellZ <= Cell(Mathf.Max(a.z, Mathf.Max(b.z, c.z))); cellZ++)
                    {
                        for (var cellX = Cell(Mathf.Min(a.x, Mathf.Min(b.x, c.x))); cellX <= Cell(Mathf.Max(a.x, Mathf.Max(b.x, c.x))); cellX++)
                        {
                            var key = Key(cellX, cellZ);
                            if (!cells.TryGetValue(key, out var list))
                            {
                                list = new List<int>();
                                cells.Add(key, list);
                            }

                            list.Add(triangle);
                        }
                    }
                }
            }

            public WaterBody Body { get; }

            public Vector3[] Vertices { get; }

            public Vector2[] Flows { get; }

            public Color[] Colors { get; }

            public Bounds Bounds { get; }

            public bool TrySample(float x, float z, out float height, out Vector2 flow, out Color color)
            {
                height = 0f;
                flow = Vector2.zero;
                color = Color.clear;
                if (!cells.TryGetValue(Key(Cell(x), Cell(z)), out var list))
                {
                    return false;
                }

                foreach (var triangle in list)
                {
                    var a = triangles[triangle];
                    var b = triangles[triangle + 1];
                    var c = triangles[triangle + 2];
                    if (!Barycentric(Vertices[a], Vertices[b], Vertices[c], x, z, out var u, out var v, out var w))
                    {
                        continue;
                    }

                    height = (Vertices[a].y * u) + (Vertices[b].y * v) + (Vertices[c].y * w);
                    flow = (Flows[a] * u) + (Flows[b] * v) + (Flows[c] * w);
                    color = (Colors[a] * u) + (Colors[b] * v) + (Colors[c] * w);
                    return true;
                }

                return false;
            }

            private static bool Barycentric(Vector3 a, Vector3 b, Vector3 c, float x, float z, out float u, out float v, out float w)
            {
                var determinant = ((b.z - c.z) * (a.x - c.x)) + ((c.x - b.x) * (a.z - c.z));
                u = 0f;
                v = 0f;
                w = 0f;
                if (Mathf.Abs(determinant) < 0.000001f)
                {
                    return false;
                }

                u = (((b.z - c.z) * (x - c.x)) + ((c.x - b.x) * (z - c.z))) / determinant;
                v = (((c.z - a.z) * (x - c.x)) + ((a.x - c.x) * (z - c.z))) / determinant;
                w = 1f - u - v;
                const float tolerance = -0.0001f;
                return u >= tolerance && v >= tolerance && w >= tolerance;
            }

            private static int Cell(float coordinate)
            {
                return Mathf.FloorToInt(coordinate / CellSize);
            }

            private static long Key(int cellX, int cellZ)
            {
                return ((long)cellX << 32) | (uint)cellZ;
            }
        }
    }
}
