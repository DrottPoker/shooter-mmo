using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Finds the faces of an editable Blender mesh that the triangles of an imported Unity mesh come
    // from, by the positions of their corners, so the order Unity gives its vertices and triangles
    // does not matter. The import bakes the axis conversion, so Blender's (x, y, z) is Unity's
    // (x, z, y). Body faces are the coordinate system garments refer to; see
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    public static class ArtTopologyMaps
    {
        // How far a Unity vertex may lie from its Blender vertex: the export writes positions to
        // a hundredth of a millimeter.
        public const float Tolerance = 0.0001f;

        private const float CellSize = 0.001f;

        public static Vector3 ToUnity(float x, float y, float z)
        {
            return new Vector3(x, z, y);
        }

        // The source vertex of each position, or -1 where none lies within the tolerance.
        public static int[] VertexSources(IReadOnlyList<Vector3> positions, Topology topology)
        {
            var sources = new int[positions.Count];
            for (var index = 0; index < positions.Count; index++)
            {
                sources[index] = topology.Nearest(positions[index]);
            }

            return sources;
        }

        // Every source vertex within the tolerance of each position, nearest first. A garment made
        // of pieces that meet, such as a hood's collar and the band turned down over it, has two
        // vertices at one place, and only the face of a triangle tells which one a corner is.
        public static int[][] VertexCandidates(IReadOnlyList<Vector3> positions, Topology topology)
        {
            var candidates = new int[positions.Count][];
            for (var index = 0; index < positions.Count; index++)
            {
                candidates[index] = topology.Within(positions[index]);
            }

            return candidates;
        }

        // The source face of each triangle of the mesh, over its submeshes in order, from the
        // source vertices of its corners: the one face that holds all three.
        public static int[] TriangleFaces(Mesh mesh, IReadOnlyList<int> vertexSources, Topology topology, string where)
        {
            var candidates = new int[vertexSources.Count][];
            for (var index = 0; index < candidates.Length; index++)
            {
                candidates[index] = vertexSources[index] >= 0 ? new[] { vertexSources[index] } : Array.Empty<int>();
            }

            return TriangleFaces(mesh, candidates, topology, where);
        }

        // The same from the source vertices each corner may be, nearest first: the one face that
        // holds the nearest of each corner's, or where no face does, the one that holds one of each
        // corner's. A turned edge's rim may lie on the edge it turns from, so the nearest decide
        // where they can.
        public static int[] TriangleFaces(Mesh mesh, IReadOnlyList<int[]> vertexCandidates, Topology topology, string where)
        {
            var faces = new List<int>();
            var triangles = new List<int>();
            for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                mesh.GetTriangles(triangles, submesh);
                for (var corner = 0; corner < triangles.Count; corner += 3)
                {
                    var a = vertexCandidates[triangles[corner]];
                    var b = vertexCandidates[triangles[corner + 1]];
                    var c = vertexCandidates[triangles[corner + 2]];
                    if (a.Length == 0 || b.Length == 0 || c.Length == 0)
                    {
                        throw new InvalidOperationException(where + ": triangle " + faces.Count + " has a corner outside the editable mesh.");
                    }

                    var face = FaceHolding(topology, new[] { a[0] }, new[] { b[0] }, new[] { c[0] }, where, faces.Count);
                    if (face < 0)
                    {
                        face = FaceHolding(topology, a, b, c, where, faces.Count);
                    }

                    if (face < 0)
                    {
                        throw new InvalidOperationException(where + ": triangle " + faces.Count + " lies in no face of the editable mesh.");
                    }

                    faces.Add(face);
                }
            }

            return faces.ToArray();
        }

        // The one face that holds one vertex of each corner's, or -1.
        private static int FaceHolding(Topology topology, int[] a, int[] b, int[] c, string where, int triangle)
        {
            var face = -1;
            foreach (var first in a)
            {
                foreach (var candidate in topology.FacesOf(first))
                {
                    if (candidate == face || !HoldsAny(topology, candidate, b) || !HoldsAny(topology, candidate, c))
                    {
                        continue;
                    }

                    if (face >= 0)
                    {
                        throw new InvalidOperationException(where + ": triangle " + triangle + " lies in two faces.");
                    }

                    face = candidate;
                }
            }

            return face;
        }

        private static bool HoldsAny(Topology topology, int face, int[] vertices)
        {
            foreach (var vertex in vertices)
            {
                if (topology.FaceHolds(face, vertex))
                {
                    return true;
                }
            }

            return false;
        }

        // An editable mesh: its vertices in Unity's axes and its faces.
        public sealed class Topology
        {
            private readonly Vector3[] vertices;
            private readonly int[][] faces;
            private readonly List<int>[] facesOfVertex;
            private readonly Dictionary<Vector3Int, List<int>> cells = new Dictionary<Vector3Int, List<int>>();

            public Topology(IReadOnlyList<float> blenderVertices, IReadOnlyList<int> faceSizes, IReadOnlyList<int> faceVertices)
            {
                if (blenderVertices.Count % 3 != 0)
                {
                    throw new ArgumentException("Vertices are x, y, z triples.", nameof(blenderVertices));
                }

                vertices = new Vector3[blenderVertices.Count / 3];
                facesOfVertex = new List<int>[vertices.Length];
                for (var index = 0; index < vertices.Length; index++)
                {
                    vertices[index] = ToUnity(blenderVertices[index * 3], blenderVertices[index * 3 + 1], blenderVertices[index * 3 + 2]);
                    facesOfVertex[index] = new List<int>();
                    var cell = CellOf(vertices[index]);
                    if (!cells.TryGetValue(cell, out var inCell))
                    {
                        inCell = new List<int>();
                        cells.Add(cell, inCell);
                    }

                    inCell.Add(index);
                }

                faces = new int[faceSizes.Count][];
                var next = 0;
                for (var face = 0; face < faceSizes.Count; face++)
                {
                    faces[face] = new int[faceSizes[face]];
                    for (var corner = 0; corner < faceSizes[face]; corner++)
                    {
                        var vertex = faceVertices[next++];
                        faces[face][corner] = vertex;
                        facesOfVertex[vertex].Add(face);
                    }
                }

                if (next != faceVertices.Count)
                {
                    throw new ArgumentException("The face sizes do not add up to the face vertices.", nameof(faceSizes));
                }
            }

            public int VertexCount
            {
                get { return vertices.Length; }
            }

            public int FaceCount
            {
                get { return faces.Length; }
            }

            public int SizeOf(int face)
            {
                return faces[face].Length;
            }

            public IReadOnlyList<int> FacesOf(int vertex)
            {
                return facesOfVertex[vertex];
            }

            public bool FaceHolds(int face, int vertex)
            {
                return Array.IndexOf(faces[face], vertex) >= 0;
            }

            // Every vertex within the tolerance of the position, the one Nearest gives first.
            public int[] Within(Vector3 position)
            {
                var nearest = Nearest(position);
                var center = CellOf(position);
                var found = new List<int>();
                for (var x = -1; x <= 1; x++)
                {
                    for (var y = -1; y <= 1; y++)
                    {
                        for (var z = -1; z <= 1; z++)
                        {
                            if (!cells.TryGetValue(center + new Vector3Int(x, y, z), out var inCell))
                            {
                                continue;
                            }

                            foreach (var vertex in inCell)
                            {
                                if (vertex != nearest && (vertices[vertex] - position).magnitude <= Tolerance)
                                {
                                    found.Add(vertex);
                                }
                            }
                        }
                    }
                }

                if (nearest >= 0)
                {
                    found.Insert(0, nearest);
                }

                return found.ToArray();
            }

            // The vertex nearest to the position within the tolerance, or -1.
            public int Nearest(Vector3 position)
            {
                var center = CellOf(position);
                var best = -1;
                var bestDistance = Tolerance;
                for (var x = -1; x <= 1; x++)
                {
                    for (var y = -1; y <= 1; y++)
                    {
                        for (var z = -1; z <= 1; z++)
                        {
                            if (!cells.TryGetValue(center + new Vector3Int(x, y, z), out var inCell))
                            {
                                continue;
                            }

                            foreach (var vertex in inCell)
                            {
                                var distance = (vertices[vertex] - position).magnitude;
                                if (distance <= bestDistance)
                                {
                                    best = vertex;
                                    bestDistance = distance;
                                }
                            }
                        }
                    }
                }

                return best;
            }

            private static Vector3Int CellOf(Vector3 position)
            {
                return new Vector3Int(
                    Mathf.FloorToInt(position.x / CellSize),
                    Mathf.FloorToInt(position.y / CellSize),
                    Mathf.FloorToInt(position.z / CellSize));
            }
        }
    }
}
