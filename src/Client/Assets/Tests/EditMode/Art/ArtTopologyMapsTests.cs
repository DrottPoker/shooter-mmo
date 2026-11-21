using Mmorpg.Client.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The appearance catalog finds the Blender face of every imported triangle by the positions of
    // its corners; see docs/ART_PIPELINE.md under Unity Import Rules.
    public sealed class ArtTopologyMapsTests
    {
        [Test]
        public void ATriangleFindsItsFaceWhereTwoPiecesMeetAtTheSamePlaces()
        {
            // Two triangles of separate pieces that meet along an edge, each with its own two
            // vertices there, such as a hood's collar and the band turned down over it.
            var blender = new float[]
            {
                0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f, 0f, 0f, 0f, -1f, 0f,
            };
            var topology = new ArtTopologyMaps.Topology(blender, new[] { 3, 3 }, new[] { 0, 1, 2, 3, 4, 5 });
            var mesh = new Mesh();
            try
            {
                var positions = new Vector3[6];
                for (var vertex = 0; vertex < 6; vertex++)
                {
                    positions[vertex] = ArtTopologyMaps.ToUnity(blender[vertex * 3], blender[vertex * 3 + 1], blender[vertex * 3 + 2]);
                }

                // Unity's order differs: the second piece's triangle comes first.
                mesh.vertices = new[] { positions[3], positions[4], positions[5], positions[0], positions[1], positions[2] };
                mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
                var candidates = ArtTopologyMaps.VertexCandidates(mesh.vertices, topology);
                Assert.That(candidates[0], Is.EquivalentTo(new[] { 0, 3 }));
                var faces = ArtTopologyMaps.TriangleFaces(mesh, candidates, topology, "test");
                Assert.That(faces, Is.EqualTo(new[] { 1, 0 }));
                // Each vertex at a shared place is the one its triangle's face holds, as a body's
                // lips that meet are.
                Assert.That(ArtTopologyMaps.VertexSources(mesh, candidates, faces, topology), Is.EqualTo(new[] { 3, 4, 5, 0, 1, 2 }));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void ATriangleFindsItsFaceByItsSideWhereLipsMeet()
        {
            // Lips that meet close a slit: the faces on both sides of it hold vertices at the same
            // places and face away from each other, so only the side of a triangle tells its face.
            var blender = new float[]
            {
                0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f,
                0f, 0f, 0f, 0f, 1f, 0f, 1f, 0f, 0f,
            };
            var topology = new ArtTopologyMaps.Topology(blender, new[] { 3, 3 }, new[] { 0, 1, 2, 3, 4, 5 });
            var mesh = new Mesh();
            try
            {
                var p = ArtTopologyMaps.ToUnity(0f, 0f, 0f);
                var q = ArtTopologyMaps.ToUnity(1f, 0f, 0f);
                var r = ArtTopologyMaps.ToUnity(0f, 1f, 0f);
                // The axis conversion mirrors, so Unity's triangles go round the other way than
                // their faces' corners.
                mesh.vertices = new[] { p, r, q, p, q, r };
                mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
                var candidates = ArtTopologyMaps.VertexCandidates(mesh.vertices, topology);
                Assert.That(candidates[0], Is.EquivalentTo(new[] { 0, 3 }));
                var faces = ArtTopologyMaps.TriangleFaces(mesh, candidates, topology, "test");
                Assert.That(faces, Is.EqualTo(new[] { 0, 1 }));
                Assert.That(ArtTopologyMaps.VertexSources(mesh, candidates, faces, topology), Is.EqualTo(new[] { 0, 2, 1, 3, 5, 4 }));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
