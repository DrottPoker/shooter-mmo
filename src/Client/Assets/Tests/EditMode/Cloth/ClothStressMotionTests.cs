using System.Linq;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The cloth stress motions keep their courses, the Wardrobe plays them as the game shows a body,
    // and ClothMotionMeter finds what it measures, as docs/EQUIPMENT_VISUALS_DESIGN.md describes
    // under Measuring The Cloth.
    public sealed class ClothStressMotionTests
    {
        private const float Step = 1f / 60f;

        [Test]
        public void EveryMotionStartsWhereTheBodyStandsAndKeepsItsPhasesInside()
        {
            Assert.That(ClothStressMotion.All.Select(motion => motion.Name).Distinct().Count(), Is.EqualTo(ClothStressMotion.All.Count));
            foreach (var motion in ClothStressMotion.All)
            {
                Assert.That(motion.Seconds, Is.GreaterThan(1f), motion.Name);
                Assert.That(motion.At(0f).Position.magnitude, Is.LessThan(1e-4f), motion.Name);
                foreach (var (start, end) in motion.Steady)
                {
                    Assert.That(start, Is.GreaterThanOrEqualTo(0f).And.LessThan(end), motion.Name);
                    Assert.That(end, Is.LessThanOrEqualTo(motion.Seconds + 1e-4f), motion.Name);
                }

                Assert.That(motion.Jumps.All(jump => jump > 0f && jump < motion.Seconds), Is.True, motion.Name);
                Assert.That(motion.Frames().Sum(), Is.GreaterThanOrEqualTo(motion.Seconds), motion.Name);
            }
        }

        [TestCase("Spin 720", 720f)]
        [TestCase("Spin 2880", 2880f)]
        public void ASpinTurnsTheBodyAtItsRateOnTheSpot(string name, float rate)
        {
            var motion = ClothStressMotion.Named(name);
            var (start, end) = motion.Spin.Value;
            var quarter = 90f / rate;
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(motion.At(start).Yaw + 90f, motion.At(start + quarter).Yaw)), Is.LessThan(1f));
            Assert.That(Vector3.Distance(motion.At(start).Position, motion.At(end).Position), Is.LessThan(1e-3f));
            Assert.That(motion.Settle.Value.Start, Is.EqualTo(end).Within(1e-4f));
            Assert.That(motion.Settle.Value.End, Is.EqualTo(motion.Seconds).Within(1e-4f));
        }

        [Test]
        public void AJumpLandsAtOnce()
        {
            var motion = ClothStressMotion.Named("Snap 3 m");
            var jump = motion.Jumps.Single();
            var before = motion.At(jump - 0.0005f).Position;
            var after = motion.At(jump + 0.0005f).Position;
            Assert.That(Vector3.Distance(before, after), Is.EqualTo(3f).Within(0.02f));
            Assert.That(Vector3.Distance(motion.At(jump - 0.1f).Position, before), Is.EqualTo(0.1f * HumanLocomotion.JogSpeed).Within(0.02f));
        }

        [Test]
        public void JerkIsMeasuredFromEachFramesOwnTime()
        {
            // Steady speed and steady acceleration in frames of different length have no jerk.
            var times = new[] { 0f, 1f / 30f, 1f / 30f + 1f / 144f, 1f / 30f + 1f / 144f + 1f / 60f };
            Vector3 Moving(float time) => new Vector3(2f * time, 0f, 0f);
            Vector3 Speeding(float time) => new Vector3(0f, 1.5f * time * time, 0f);
            foreach (var place in new System.Func<float, Vector3>[] { Moving, Speeding })
            {
                Assert.That(ClothMotionMeter.Jerk(place(times[3]), place(times[2]), place(times[1]), place(times[0]),
                    times[3] - times[2], times[2] - times[1], times[1] - times[0]), Is.LessThan(1e-2f));
            }

            // In equal frames a cube of the time has the jerk 6.
            Vector3 Cube(float time) => new Vector3(0f, 0f, time * time * time);
            Assert.That(ClothMotionMeter.Jerk(Cube(0.3f), Cube(0.2f), Cube(0.1f), Cube(0f), 0.1f, 0.1f, 0.1f), Is.EqualTo(6f).Within(1e-2f));
        }

        [Test]
        public void TheNetworkMotionShowsTheCourseAsTheGamePresentsARemoteBody()
        {
            using (var preview = new WardrobePreview(WardrobePreview.Bodies().Single(body => body.Name == "Human_Male")))
            {
                var motion = ClothStressMotion.Named("Network 15 Hz");
                preview.Play(motion);
                for (var frame = 0; frame < 90; frame++)
                {
                    preview.Step(Step, Vector3.back * 3f);
                }

                // Four ticks behind the newest snapshot, as remote bodies are shown at 30 ticks and 15 snapshots a second.
                var delay = RemoteMovementPresenter.InterpolationDelayTicks(ClothStressMotion.NetworkTickRateHz,
                    ClothStressMotion.NetworkSnapshotRateHz) / (float)ClothStressMotion.NetworkTickRateHz;
                var shown = preview.Body.transform.position;
                Assert.That(Vector3.Distance(shown, motion.At(preview.StressTime - delay).Position), Is.LessThan(0.15f));
                Assert.That(Vector3.Distance(shown, motion.At(preview.StressTime).Position), Is.GreaterThan(0.3f));
            }
        }

        [Test]
        public void AJogAfterATurnMovesForwardAgain()
        {
            using (var preview = new WardrobePreview(WardrobePreview.Bodies().Single(body => body.Name == "Human_Male")))
            {
                preview.Play(ClothStressMotion.Named("Turn 90"));
                for (var frame = 0; frame < 180; frame++)
                {
                    preview.Step(Step, Vector3.back * 3f);
                }

                Assert.That(Quaternion.Angle(preview.Body.transform.rotation, Quaternion.identity), Is.GreaterThan(45f), "turned");
                preview.Play(WardrobePreview.Motion.Jog);
                var start = preview.Body.transform.position;
                preview.Step(0.5f, Vector3.back * 3f);
                Assert.That(Quaternion.Angle(preview.Body.transform.rotation, Quaternion.identity), Is.LessThan(1e-3f));
                Assert.That(Vector3.Distance(preview.Body.transform.position, start + Vector3.forward * (0.5f * HumanLocomotion.JogSpeed)),
                    Is.LessThan(1e-3f));
            }
        }

        [Test]
        public void TheMeterFindsALegThatComesThroughTheCloth()
        {
            using (var preview = new WardrobePreview(WardrobePreview.Bodies().Single(body => body.Name == "Human_Male")))
            using (var meter = new ClothMotionMeter(preview))
            {
                preview.Wear(new[] { "WoolRobe" });
                preview.Step(Step, Vector3.back * 3f);
                meter.Sample(null, 0f, Step, true);
                Assert.That(meter.LastPoked, Is.EqualTo(0), "standing");

                // The knee raised high in front, and the robe's chains left where they hang.
                var bones = preview.Body.GetComponentsInChildren<Transform>(true).GroupBy(bone => bone.name)
                    .ToDictionary(group => group.Key, group => group.First());
                var thigh = bones["UpperLeg_R"];
                var along = (bones["LowerLeg_R"].position - thigh.position).normalized;
                thigh.rotation = Quaternion.FromToRotation(along, preview.Body.transform.forward) * thigh.rotation;
                // The skirt from the waist moves with the hips above its chains, so the knee comes
                // through below them only.
                meter.Sample(null, 0f, Step, true);
                Assert.That(meter.LastPoked, Is.GreaterThan(5));
                Assert.That(meter.LastPokeDepth, Is.GreaterThan(0.03f));
            }
        }

        // The legs come through a robe by little while walking and jogging, and never through a
        // cloak; see docs/EQUIPMENT_VISUALS_DESIGN.md.
        [TestCase("Human_Male", "WoolRobe", "Walk", 0.015f)]
        [TestCase("Human_Male", "WoolRobe", "Jog", 0.025f)]
        [TestCase("Human_Female", "WoolRobe", "Jog", 0.025f)]
        [TestCase("Human_Male", "WoolCloak", "Walk", 0f)]
        [TestCase("Human_Male", "WoolCloak", "Jog", 0f)]
        [TestCase("Human_Female", "WoolCloak", "Jog", 0f)]
        public void TheLegsStayUnderTheCloth(string bodyName, string garment, string motion, float deepest)
        {
            using (var preview = new WardrobePreview(WardrobePreview.Bodies().Single(body => body.Name == bodyName)))
            {
                preview.Wear(new[] { garment });
                var report = ClothMeasurement.Run(preview, ClothStressMotion.Named(motion));
                Assert.That(report.Frames, Is.GreaterThan(200));
                Assert.That(report.LegPokeDepth, Is.LessThanOrEqualTo(deepest));
            }
        }

        // A spin flares the hems out, not stiffly, keeps the skirt's chains in their order and the
        // legs and the robe under the cloth, and the cloth settles soon after it.
        [TestCase("Spin 720")]
        [TestCase("Spin 2880")]
        public void ASpinFlaresTheClothOutAndItSettles(string name)
        {
            using (var preview = new WardrobePreview(WardrobePreview.Bodies().Single(body => body.Name == "Human_Male")))
            {
                preview.Wear(new[] { "WoolRobe", "WoolCloak" });
                var report = ClothMeasurement.Run(preview, ClothStressMotion.Named(name), meshEvery: 10);
                Assert.That(report.Frames, Is.GreaterThan(300));
                Assert.That(report.Flare, Is.GreaterThan(0.05f));
                Assert.That(report.SettleSeconds, Is.LessThan(1.5f));
                Assert.That(report.OrderBreaks, Is.EqualTo(0));
                Assert.That(report.LegPokeDepth, Is.LessThanOrEqualTo(0f));
                Assert.That(report.LayerPokeDepth, Is.LessThan(0.01f));
                Assert.That(float.IsNaN(report.JerkRms) || float.IsInfinity(report.JerkRms), Is.False);
            }
        }

        // A remote body seen through the network's 15 snapshots a second moves its cloth no more
        // jerkily than the same course moved smoothly.
        [Test]
        public void TheNetworkDoesNotMakeTheClothJerkier()
        {
            using (var preview = new WardrobePreview(WardrobePreview.Bodies().Single(body => body.Name == "Human_Male")))
            {
                preview.Wear(new[] { "WoolRobe", "WoolCloak" });
                var smooth = ClothMeasurement.Run(preview, ClothStressMotion.Named("Zig-zag"), meshEvery: 1000);
                var network = ClothMeasurement.Run(preview, ClothStressMotion.Named("Network 15 Hz"), meshEvery: 1000);
                Assert.That(network.JerkRms, Is.LessThan(1.2f * smooth.JerkRms));
            }
        }
    }
}
