using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Editor
{
    // The vertices of a water surface with what the water shader reads from them: the flow in
    // m/s in the second texture coordinate, and in the vertex color red the fast water that
    // foams, green how much the waves lift the vertex, and blue how strongly foam lines roll in
    // toward the shore. Positions are in the space of the mesh's transform; flows are always in
    // world X and Z, because the shader moves its patterns in world space.
    internal sealed class WaterSurfaceMesh
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector2> flows = new List<Vector2>();
        private readonly List<Color> colors = new List<Color>();
        private readonly List<int> triangles = new List<int>();

        public int VertexCount
        {
            get { return vertices.Count; }
        }

        public IReadOnlyList<Vector3> Vertices
        {
            get { return vertices; }
        }

        public IReadOnlyList<Vector2> Flows
        {
            get { return flows; }
        }

        public IReadOnlyList<Color> Colors
        {
            get { return colors; }
        }

        public IReadOnlyList<int> Triangles
        {
            get { return triangles; }
        }

        public int Add(Vector3 position, Vector2 flow, float turbulence, float waves, float foamLines)
        {
            vertices.Add(position);
            flows.Add(flow);
            colors.Add(new Color(turbulence, waves, foamLines, 1f));
            return vertices.Count - 1;
        }

        // Unity's front faces run clockwise seen from their front, so a triangle faces up when
        // its edge cross product points up.
        public void Triangle(int a, int b, int c)
        {
            var up = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).y >= 0f;
            triangles.Add(a);
            triangles.Add(up ? b : c);
            triangles.Add(up ? c : b);
        }

        // Writes the surface into the mesh. Returns false and leaves the mesh as it is when it
        // already holds exactly this surface, so a rebuild without a change marks nothing dirty.
        public bool WriteTo(Mesh mesh)
        {
            if (Matches(mesh))
            {
                return false;
            }

            // Clearing the old vertex layout too, so the normals are computed anew.
            mesh.Clear(false);
            mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetUVs(1, flows);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return true;
        }

        private bool Matches(Mesh mesh)
        {
            if (mesh.vertexCount != vertices.Count || mesh.subMeshCount != 1)
            {
                return false;
            }

            var existingFlows = new List<Vector2>();
            mesh.GetUVs(1, existingFlows);
            var existingVertices = mesh.vertices;
            var existingColors = mesh.colors;
            var existingTriangles = mesh.triangles;
            if (existingFlows.Count != flows.Count
                || existingColors.Length != colors.Count
                || existingTriangles.Length != triangles.Count)
            {
                return false;
            }

            for (var index = 0; index < vertices.Count; index++)
            {
                if (existingVertices[index] != vertices[index]
                    || existingFlows[index] != flows[index]
                    || existingColors[index] != colors[index])
                {
                    return false;
                }
            }

            for (var index = 0; index < triangles.Count; index++)
            {
                if (existingTriangles[index] != triangles[index])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
