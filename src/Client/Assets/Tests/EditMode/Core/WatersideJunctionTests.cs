using System.Collections.Generic;
using Mmorpg.Client.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // Where the waterside's river reaches into the lake and into the sea, their surfaces overlap.
    // Every water body draws with the one water material, so they meet without a seam as long as
    // their vertices agree there: the river lies at the level of the water it reaches, its water
    // stands still at both ends, and neither surface has waves or foam lines where they overlap.
    public sealed class WatersideJunctionTests
    {
        private const string MeshDirectory = "Assets/Art/Terrain/DevelopmentWorld2/";
        private const int RiverColumns = 5;

        [Test]
        public void TheRiverMeetsTheLakeAndTheSeaWithTheSameSurface()
        {
            var river = AssetDatabase.LoadAssetAtPath<Mesh>(MeshDirectory + "M_DevelopmentWorld2_River.asset");
            Assert.That(river, Is.Not.Null);
            var vertices = river.vertices;
            var colors = river.colors;
            var flows = new List<Vector2>();
            river.GetUVs(1, flows);

            AssertStill(vertices, colors, flows, 0, DevelopmentWorld2Builder.LakeSurface);
            AssertStill(vertices, colors, flows, vertices.Length - RiverColumns, DevelopmentWorld2Builder.SeaSurface);
        }

        [Test]
        public void TheSeaIsCalmWhereTheRiversMouthReachesIntoIt()
        {
            var river = AssetDatabase.LoadAssetAtPath<Mesh>(MeshDirectory + "M_DevelopmentWorld2_River.asset");
            var sea = AssetDatabase.LoadAssetAtPath<Mesh>(MeshDirectory + "M_DevelopmentWorld2_Sea.asset");
            Assert.That(sea, Is.Not.Null);
            var mouth = river.vertices[river.vertexCount - 1 - (RiverColumns / 2)];
            var vertices = sea.vertices;
            var colors = sea.colors;
            var near = 0;
            for (var index = 0; index < vertices.Length; index++)
            {
                var offset = new Vector2(vertices[index].x - mouth.x, vertices[index].z - mouth.z);
                if (offset.magnitude > 6f)
                {
                    continue;
                }

                near++;
                Assert.That(colors[index].g, Is.Zero, "waves at " + vertices[index]);
                Assert.That(colors[index].b, Is.Zero, "foam lines at " + vertices[index]);
            }

            Assert.That(near, Is.GreaterThan(0));
        }

        // The river's row of vertices at one end lies at the given level, with still water and
        // neither foam, waves, nor foam lines.
        private static void AssertStill(Vector3[] vertices, Color[] colors, List<Vector2> flows, int first, float level)
        {
            for (var index = first; index < first + RiverColumns; index++)
            {
                Assert.That(vertices[index].y, Is.EqualTo(level).Within(0.001f), "level at " + vertices[index]);
                Assert.That(flows[index].magnitude, Is.LessThan(0.001f), "flow at " + vertices[index]);
                Assert.That(colors[index].r, Is.Zero, "foam at " + vertices[index]);
                Assert.That(colors[index].g, Is.Zero, "waves at " + vertices[index]);
                Assert.That(colors[index].b, Is.Zero, "foam lines at " + vertices[index]);
            }
        }
    }
}
