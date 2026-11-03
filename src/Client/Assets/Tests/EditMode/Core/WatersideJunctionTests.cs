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
    // The waterfall's column starts on the stream above it, so the stream turns into the fall
    // without a line.
    public sealed class WatersideJunctionTests
    {
        private const string MeshDirectory = "Assets/Art/Terrain/DevelopmentWorld2/";
        private const int RiverColumns = 5;
        private const int StreamColumns = 5;

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

        // The column runs along the stream with its top in the stream's surface and fades in over
        // it before the lip. Where the stream's surface ends, just past the lip, the column has
        // faded in fully, still with its top in that surface and flat across the whole end of it,
        // so nothing of the stream shows past the column and no line marks where one ends. There
        // it is as wide as the channel at the lip, not wider, so no water comes from beside it.
        [Test]
        public void TheStreamTurnsIntoTheWaterfallWithoutALine()
        {
            var stream = AssetDatabase.LoadAssetAtPath<Mesh>(MeshDirectory + "M_DevelopmentWorld2_Stream.asset");
            var fall = AssetDatabase.LoadAssetAtPath<Mesh>(MeshDirectory + "M_DevelopmentWorld2_WaterfallSheet.asset");
            Assert.That(stream, Is.Not.Null);
            Assert.That(fall, Is.Not.Null);

            // The stream's first row of vertices is its end past the lip, and its second row lies
            // on the lip, as wide as the channel.
            var streamVertices = stream.vertices;
            var level = streamVertices[0].y;
            var end = streamVertices[StreamColumns / 2];
            var across = (streamVertices[StreamColumns - 1] - streamVertices[0]).normalized;
            var upstream = (streamVertices[StreamColumns + (StreamColumns / 2)] - end).normalized;
            var endHalfWidth = Vector3.Dot(streamVertices[StreamColumns - 1] - end, across);
            var lip = streamVertices[StreamColumns + (StreamColumns / 2)];
            var channelHalfWidth = Vector3.Dot(streamVertices[(2 * StreamColumns) - 1] - lip, across);

            var vertices = fall.vertices;
            var column = new List<Vector4>();
            fall.GetUVs(0, column);
            var approachTop = float.MinValue;
            var approachStart = float.MaxValue;
            var launchTop = float.MinValue;
            var launchFront = Vector3.zero;
            for (var index = 0; index < vertices.Length; index++)
            {
                if (column[index].w == 0f)
                {
                    approachTop = Mathf.Max(approachTop, vertices[index].y);
                    approachStart = Mathf.Min(approachStart, Vector3.Dot(vertices[index] - end, upstream));
                }
                else if (column[index].z == 0f && vertices[index].y > launchTop)
                {
                    launchTop = vertices[index].y;
                    launchFront = vertices[index];
                }
            }

            Assert.That(approachTop, Is.EqualTo(level).Within(0.001f), "top where the column starts to fade in");
            Assert.That(approachStart, Is.GreaterThan(0.3f), "how far before the stream's end the column starts to fade in");
            Assert.That(launchTop, Is.EqualTo(level).Within(0.001f), "top where the column has faded in");
            Assert.That(Vector3.Dot(launchFront - end, upstream), Is.EqualTo(0f).Within(0.01f), "where the column has faded in");

            // The column's top lies within 2 mm of the stream's surface across the stream's end,
            // and the column reaches no further to the sides than the channel.
            var flatLeft = 0f;
            var flatRight = 0f;
            var widest = 0f;
            for (var index = 0; index < vertices.Length; index++)
            {
                if (column[index].w != 1f || column[index].z != 0f)
                {
                    continue;
                }

                var side = Vector3.Dot(vertices[index] - launchFront, across);
                widest = Mathf.Max(widest, Mathf.Abs(side));
                if (vertices[index].y >= level - 0.002f)
                {
                    flatLeft = Mathf.Min(flatLeft, side);
                    flatRight = Mathf.Max(flatRight, side);
                }
            }

            Assert.That(-flatLeft, Is.GreaterThanOrEqualTo(endHalfWidth), "flat top toward the first wall");
            Assert.That(flatRight, Is.GreaterThanOrEqualTo(endHalfWidth), "flat top toward the second wall");
            Assert.That(widest, Is.LessThanOrEqualTo(channelHalfWidth + 0.001f), "half width where the water starts to fall");
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
