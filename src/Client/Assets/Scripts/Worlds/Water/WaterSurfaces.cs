using System;
using System.Collections.Generic;
using Mmorpg.Client.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Worlds
{
    // The drawn surfaces of a World scene's water bodies, so presentation can put things on the
    // water as it is drawn. The baked collision gives the still surface; this adds how far the
    // waves lift it, from each body's mesh and the waves of its material, exactly as the water
    // shader lifts each vertex and draws the triangles between. The client builds it when a
    // World scene has loaded.
    public sealed class WaterSurfaces
    {
        private const float CellSize = 4f;

        private readonly Dictionary<string, Surface> surfaces = new Dictionary<string, Surface>(StringComparer.Ordinal);

        public WaterSurfaces(IEnumerable<WaterBody> bodies)
        {
            if (bodies == null)
            {
                throw new ArgumentNullException(nameof(bodies));
            }

            foreach (var body in bodies)
            {
                if (body == null
                    || !body.enabled
                    || !body.gameObject.activeInHierarchy
                    || surfaces.ContainsKey(body.BodyId))
                {
                    continue;
                }

                var waves = WaterWaveSettings.Of(body.TryGetComponent<MeshRenderer>(out var renderer) ? renderer.sharedMaterial : null);
                var mesh = body.Surface;
                if (!waves.HasWaves || mesh == null)
                {
                    continue;
                }

                // Static batching replaces the mesh with an unreadable combined one.
                if (!mesh.isReadable)
                {
                    ClientLog.Warning(
                        ClientLogCategory.Client,
                        "The surface of water body '" + body.BodyId + "' cannot be read, so wakes stay on its still "
                        + "water. Keep the water body out of static batching.");
                    continue;
                }

                var surface = new Surface(mesh, body.transform.localToWorldMatrix, waves);
                if (surface.HasWaves)
                {
                    surfaces.Add(body.BodyId, surface);
                }
            }
        }

        // The water bodies of the scene's active objects.
        public static WaterSurfaces FromScene(Scene scene)
        {
            var bodies = new List<WaterBody>();
            if (scene.IsValid() && scene.isLoaded)
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    bodies.AddRange(root.GetComponentsInChildren<WaterBody>(false));
                }
            }

            return new WaterSurfaces(bodies);
        }

        // Finds the triangle of a body's surface below a point in world X and Z. A point where no
        // vertex of the triangle moves, on a body without waves, or outside every triangle has
        // nothing to follow.
        public bool TryLocate(string bodyId, float x, float z, out WaterSurfacePoint point)
        {
            point = default;
            return bodyId != null
                && surfaces.TryGetValue(bodyId, out var surface)
                && surface.TryLocate(x, z, out point);
        }

        // How far the waves lift the surface at the point at the water's clock.
        public float Lift(in WaterSurfacePoint point, float clock)
        {
            return point.Surface == null ? 0f : point.Surface.Lift(point, clock);
        }

        internal sealed class Surface
        {
            private readonly Vector3[] vertices;
            private readonly float[] weights;
            private readonly int[] triangles;
            private readonly WaterWaveSettings waves;
            private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();

            public Surface(Mesh mesh, Matrix4x4 localToWorld, WaterWaveSettings waves)
            {
                this.waves = waves;
                vertices = mesh.vertices;
                for (var index = 0; index < vertices.Length; index++)
                {
                    vertices[index] = localToWorld.MultiplyPoint3x4(vertices[index]);
                }

                // A mesh without colors lifts no vertex.
                var colors = mesh.colors;
                weights = new float[vertices.Length];
                for (var index = 0; index < colors.Length && index < weights.Length; index++)
                {
                    weights[index] = colors[index].g;
                    HasWaves = HasWaves || weights[index] > 0f;
                }

                triangles = mesh.triangles;
                for (var triangle = 0; triangle < triangles.Length / 3; triangle++)
                {
                    var a = vertices[triangles[triangle * 3]];
                    var b = vertices[triangles[(triangle * 3) + 1]];
                    var c = vertices[triangles[(triangle * 3) + 2]];
                    var firstX = Cell(Mathf.Min(a.x, Mathf.Min(b.x, c.x)));
                    var lastX = Cell(Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
                    var firstZ = Cell(Mathf.Min(a.z, Mathf.Min(b.z, c.z)));
                    var lastZ = Cell(Mathf.Max(a.z, Mathf.Max(b.z, c.z)));
                    for (var cellZ = firstZ; cellZ <= lastZ; cellZ++)
                    {
                        for (var cellX = firstX; cellX <= lastX; cellX++)
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

            // Whether the waves lift any vertex.
            public bool HasWaves { get; }

            public bool TryLocate(float x, float z, out WaterSurfacePoint point)
            {
                point = default;
                if (!cells.TryGetValue(Key(Cell(x), Cell(z)), out var list))
                {
                    return false;
                }

                foreach (var triangle in list)
                {
                    var first = triangles[triangle * 3];
                    var second = triangles[(triangle * 3) + 1];
                    var third = triangles[(triangle * 3) + 2];
                    if (TryBarycentric(vertices[first], vertices[second], vertices[third], x, z, out var u, out var v, out var w))
                    {
                        point = new WaterSurfacePoint(this, first, second, third, u, v, w);
                        return weights[first] > 0f || weights[second] > 0f || weights[third] > 0f;
                    }
                }

                return false;
            }

            // The shader lifts each vertex by the waves at its position times its weight, and the
            // triangle between interpolates the lifted vertices.
            public float Lift(in WaterSurfacePoint point, float clock)
            {
                return (point.WeightA * VertexLift(point.VertexA, clock))
                    + (point.WeightB * VertexLift(point.VertexB, clock))
                    + (point.WeightC * VertexLift(point.VertexC, clock));
            }

            private float VertexLift(int vertex, float clock)
            {
                var weight = weights[vertex];
                if (weight <= 0f)
                {
                    return 0f;
                }

                var position = vertices[vertex];
                return WaterWaves.Height(position.x, position.z, clock, waves) * weight;
            }

            private static bool TryBarycentric(Vector3 a, Vector3 b, Vector3 c, float x, float z, out float u, out float v, out float w)
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

    // A point on a water body's drawn surface: the triangle below it and how much each of its
    // vertices weighs there.
    public readonly struct WaterSurfacePoint
    {
        internal WaterSurfacePoint(
            WaterSurfaces.Surface surface,
            int vertexA,
            int vertexB,
            int vertexC,
            float weightA,
            float weightB,
            float weightC)
        {
            Surface = surface;
            VertexA = vertexA;
            VertexB = vertexB;
            VertexC = vertexC;
            WeightA = weightA;
            WeightB = weightB;
            WeightC = weightC;
        }

        internal WaterSurfaces.Surface Surface { get; }

        internal int VertexA { get; }

        internal int VertexB { get; }

        internal int VertexC { get; }

        internal float WeightA { get; }

        internal float WeightB { get; }

        internal float WeightC { get; }
    }
}
