using System.IO;
using Mmorpg.Client.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The capture of the game views places its camera as tools/art/blender/art_pipeline/game_view.py
    // places Blender's. The expected numbers are game_view.py's own for the same points, computed
    // with numpy in Blender: aim for a figure from yaw 215 and 35, a close view of a point from yaw
    // 215, and the feet. A Blender point (x, y, z) of a body facing +Y is the Unity point (x, z, y) of
    // a body facing +Z.
    public sealed class GameViewFramingTests
    {
        private const float Tolerance = 1e-5f;

        // A figure of ten points in Blender's axes.
        private static readonly Vector3[] BlenderPoints =
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(0.15f, 0.05f, 0.02f),
            new Vector3(-0.15f, -0.05f, 0.02f),
            new Vector3(0.25f, 0f, 1f),
            new Vector3(-0.3f, 0.1f, 1.05f),
            new Vector3(0f, 0.12f, 1.55f),
            new Vector3(0f, -0.08f, 1.8f),
            new Vector3(0.1f, 0f, 1.7f),
            new Vector3(-0.45f, 0.05f, 1.1f),
            new Vector3(0.4f, -0.02f, 0.9f),
        };

        [Test]
        public void AFigureFromTheFrontIsFramedAsTheBlenderViewFramesIt()
        {
            var placement = GameViewFraming.Aim(UnityPoints(), 215.0, 20.0);

            AssertNear(placement.Toward, Unity(-0.5389855446957563, 0.7697511313200572, 0.3420201433256687));
            AssertNear(placement.Centre, Unity(-0.04487824257291121, -0.05524154678770443, 1.0380128664422115));
            Assert.That(placement.Distance, Is.EqualTo(1.713470407973667).Within(Tolerance));
            AssertNear(placement.Camera, Unity(-0.9684140237346578, 1.2637042382334656, 1.624054260961657));
        }

        [Test]
        public void AFigureFromTheBackIsFramedAsTheBlenderViewFramesIt()
        {
            var placement = GameViewFraming.Aim(UnityPoints(), 35.0, 20.0);

            AssertNear(placement.Centre, Unity(-0.01946815780996401, 0.07965892029780282, 1.0255509046895854));
            Assert.That(placement.Distance, Is.EqualTo(1.7865778068923195).Within(Tolerance));
            AssertNear(placement.Camera, Unity(0.9434714545792423, -1.2955613677488669, 1.6365965022653555));
        }

        [Test]
        public void ACloseViewStandsAtTheLeastDistanceFromItsPoint()
        {
            var placement = GameViewFraming.Close(Unity(0.01, 0.03, 1.66), 215.0, 10.0);

            AssertNear(placement.Toward, Unity(-0.5648625214636235, 0.8067072841115988, 0.17364817766693033));
            Assert.That(placement.Distance, Is.EqualTo(0.6f));
            AssertNear(placement.Camera, Unity(-0.32891751287817406, 0.5140243704669593, 1.7641889066001581));
        }

        [Test]
        public void TheFeetAreFramedFromWhatIsDrawnUpToTheirMargin()
        {
            var feet = GameViewFraming.FeetPoints(UnityPoints(), 0.02f, 0f);
            Assert.That(feet.Count, Is.EqualTo(3));

            var placement = GameViewFraming.Aim(feet, 215.0, 25.0);

            AssertNear(placement.Centre, Unity(-0.006632951122096896, -0.00864626113848259, 0.017029903889873164));
            Assert.That(placement.Distance, Is.EqualTo(0.6f).Within(Tolerance));
        }

        [Test]
        public void AYawOfZeroSeesTheBackAndOneHundredEightyTheFront()
        {
            AssertNear(GameViewFraming.TowardCamera(0.0, 0.0), new Vector3(0f, 0f, -1f));
            AssertNear(GameViewFraming.TowardCamera(180.0, 0.0), new Vector3(0f, 0f, 1f));
            AssertNear(GameViewFraming.TowardCamera(90.0, 0.0), new Vector3(1f, 0f, 0f));
            Assert.That(GameViewFraming.TowardCamera(215.0, 20.0).y, Is.EqualTo(Mathf.Sin(20f * Mathf.Deg2Rad)).Within(Tolerance));
        }

        [Test]
        public void TheOverlapIsTheIntersectionOverTheUnion()
        {
            var game = GameViewFraming.Silhouette(new[]
            {
                new Color32(0, 0, 0, 255), new Color32(0, 0, 0, 200), new Color32(0, 0, 0, 127), new Color32(0, 0, 0, 0),
            }, GameViewCapture.GameAlpha);
            var blender = GameViewFraming.Silhouette(new[]
            {
                new Color32(0, 0, 0, 255), new Color32(0, 0, 0, 0), new Color32(0, 0, 0, 1), new Color32(0, 0, 0, 0),
            }, GameViewCapture.BlenderAlpha);

            var overlap = GameViewFraming.Compare(game, blender);

            Assert.That(overlap.Both, Is.EqualTo(1));
            Assert.That(overlap.Either, Is.EqualTo(3));
            Assert.That(overlap.First, Is.EqualTo(2));
            Assert.That(overlap.Second, Is.EqualTo(2));
            Assert.That(overlap.Ratio, Is.EqualTo(1f / 3f).Within(Tolerance));
            Assert.That(GameViewFraming.Compare(new bool[2], new bool[2]).Ratio, Is.EqualTo(1f));
        }

        [Test]
        public void AJobNamesEveryViewWithItsLook()
        {
            var path = Path.Combine(Path.GetTempPath(), "game-view-job-" + System.Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "{\"status\": \"status.json\", \"shard\": \"local-shard-1\", \"settleSeconds\": 2, \"views\": [" +
                "{\"name\": \"Linen_Male_Rest_Y215\", \"outfit\": \"Linen\", \"model\": \"Human_Male\", \"label\": \"Male\"," +
                " \"garments\": [\"LinenShirt\", \"WoolTrousers\"], \"paints\": [\"Briefs\"], \"pose\": \"Rest\", \"clip\": \"\", \"frame\": 0," +
                " \"yaw\": 215, \"pitch\": 20, \"framing\": \"figure\", \"picture\": \"Linen_Male_Rest_Y215.png\", \"blenderClasses\": \"\"}]}");
            try
            {
                var job = GameViewJob.Read(path);
                Assert.That(job.views.Length, Is.EqualTo(1));
                Assert.That(job.settleSeconds, Is.EqualTo(2f));
                var view = job.views[0];
                Assert.That(view.IsRest, Is.True);
                var look = view.Look();
                Assert.That(look.HasGarment("LinenShirt") && look.HasGarment("WoolTrousers"), Is.True);
                Assert.That(look.HasPaint("Briefs"), Is.True);
                Assert.That(look.DyesFor("LinenShirt"), Is.Empty, "A view wears its garments in their natural colors.");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void AJobWithAnUnknownFrameIsRefused()
        {
            var job = new GameViewJob
            {
                status = "status.json",
                shard = "local-shard-1",
                views = new[] { new GameViewJobView { name = "A", model = "Human_Male", pose = "Rest", picture = "a.png", framing = "torso" } },
            };

            Assert.That(job.Problem(), Does.Contain("torso"));
            job.views[0].framing = GameViewFraming.Head;
            Assert.That(job.Problem(), Is.Null);
        }

        private static Vector3[] UnityPoints()
        {
            var points = new Vector3[BlenderPoints.Length];
            for (var index = 0; index < points.Length; index++)
            {
                points[index] = new Vector3(BlenderPoints[index].x, BlenderPoints[index].z, BlenderPoints[index].y);
            }

            return points;
        }

        // The Unity point of a body facing +Z for Blender's point of a body facing +Y.
        private static Vector3 Unity(double x, double y, double z)
        {
            return new Vector3((float)x, (float)z, (float)y);
        }

        private static void AssertNear(Vector3 actual, Vector3 expected)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(Tolerance), "Expected " + expected.ToString("F6") + ", got " + actual.ToString("F6"));
        }
    }
}
