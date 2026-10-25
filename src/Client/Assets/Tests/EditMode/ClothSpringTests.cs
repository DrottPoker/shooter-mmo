using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Cloth;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The springs on the cloth's target pose hold what docs/EQUIPMENT_VISUALS_DESIGN.md promises of
    // them: a body in its bind pose shows its garments as they were made, a jump carries the cloth
    // along, the swing hardly depends on the frame time, random poses and frame times break nothing,
    // and a change of the level of detail does not jolt the cloth.
    public sealed class ClothSpringTests
    {
        private const float Step = 1f / 60f;
        private static readonly Vector3 CameraOffset = new Vector3(0f, 0.3f, -3.2f);

        private static readonly string[][] Outfits =
        {
            new[] { "WoolRobe" }, new[] { "WoolCloak" }, new[] { "WoolRobe", "WoolCloak" },
        };

        [Test]
        public void InTheBindPoseTheSpringsShowTheGarmentsAsTheyWereMade()
        {
            foreach (var outfit in Outfits)
            {
                using (var preview = Preview(outfit))
                {
                    // The body holds its bind pose: no clip plays, and nothing moves it.
                    preview.Frozen = true;
                    preview.Body.GetComponent<Animator>().enabled = false;
                    preview.Play(WardrobePreview.Motion.Idle);
                    foreach (var transform in preview.Body.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(part => part.bones)
                                 .Where(bone => bone != null).Distinct())
                    {
                        Rest(transform, preview);
                    }

                    for (var frame = 0; frame < 90; frame++)
                    {
                        preview.Step(Step, CameraOffset);
                    }

                    Assert.That(preview.Solver.CurrentDetail, Is.EqualTo(ClothDetail.Spring), string.Join("+", outfit));
                    Assert.That(preview.Solver.SpringWeight, Is.EqualTo(1f), string.Join("+", outfit));
                    foreach (var chain in ClothChains.All)
                    {
                        var shown = preview.Solver.ParticlesOf(chain);
                        var target = preview.Solver.TargetsOf(chain);
                        for (var index = 1; index < shown.Count; index++)
                        {
                            Assert.That(Vector3.Angle(shown[index] - shown[index - 1], target[index] - target[index - 1]), Is.LessThan(0.01f),
                                string.Join("+", outfit) + " " + chain.Bones[0] + " " + index);
                        }
                    }
                }
            }
        }

        // A jump, found on its own, moves the cloth against its bone in the frame of the jump and the
        // next as the same motion without the jump does, within a centimeter.
        [TestCase("Jog", "Snap 3 m")]
        [TestCase("Jog", "Teleport")]
        [TestCase("Idle", "Snap 180")]
        public void AJumpCarriesTheClothAlong(string smooth, string jumping)
        {
            using (var without = Preview(new[] { "WoolRobe", "WoolCloak" }))
            using (var with = Preview(new[] { "WoolRobe", "WoolCloak" }))
            {
                var motion = ClothStressMotion.Named(jumping);
                var jump = motion.Jumps.Single();
                without.Play(ClothStressMotion.Named(smooth));
                with.Play(motion);
                var largest = 0f;
                while (with.StressTime < jump + 2.5f * Step)
                {
                    without.Step(Step, CameraOffset);
                    with.Step(Step, CameraOffset);
                    largest = Mathf.Max(largest, Apart(without, with));
                }

                Assert.That(largest, Is.LessThan(0.01f));
            }
        }

        [Test]
        public void TheGameTellsTheClothOfAJumpTooSmallToFindOnItsOwn()
        {
            // A step of 25 cm in a frame is a speed of 15 m/s, which a body may run at, so only the
            // game knows it was a jump.
            foreach (var told in new[] { true, false })
            {
                using (var still = Preview(new[] { "WoolCloak" }))
                using (var stepped = Preview(new[] { "WoolCloak" }))
                {
                    var largest = 0f;
                    for (var frame = 0; frame < 120; frame++)
                    {
                        if (frame == 60)
                        {
                            stepped.Body.transform.position += new Vector3(0.25f, 0f, 0f);
                            if (told)
                            {
                                stepped.Solver.NotifyDiscontinuity();
                            }
                        }

                        still.Step(Step, CameraOffset);
                        stepped.Step(Step, CameraOffset);
                        largest = Mathf.Max(largest, Apart(still, stepped));
                    }

                    if (told)
                    {
                        Assert.That(largest, Is.LessThan(0.01f));
                    }
                    else
                    {
                        Assert.That(largest, Is.GreaterThan(0.01f));
                    }
                }
            }
        }

        // With the body's pose held, so only the springs and not the clips' sampling differ, the
        // chains' tips lie within a centimeter of where they lie stepped at 1/240 s, as RMS.
        [TestCase("Zig-zag")]
        [TestCase("Spin 1440")]
        public void TheSwingHardlyDependsOnTheFrameTime(string name)
        {
            using (var preview = Preview(new[] { "WoolRobe", "WoolCloak" }))
            {
                var deviations = ClothMeasurement.FrameTimeDeviations(preview, ClothStressMotion.Named(name), true);
                foreach (var (_, rms) in deviations)
                {
                    Assert.That(rms, Is.LessThan(0.01f));
                }
            }
        }

        [Test]
        public void RandomPosesAndFrameTimesBreakNothing()
        {
            var random = new System.Random(3);
            using (var preview = Preview(new[] { "WoolRobe", "WoolCloak" }))
            {
                preview.Frozen = true;
                preview.Body.GetComponent<Animator>().enabled = false;
                var bones = preview.Body.GetComponentsInChildren<Transform>(true).GroupBy(bone => bone.name)
                    .ToDictionary(group => group.Key, group => group.First());
                var moved = new[] { "Hips", "Spine", "Chest", "UpperChest", "UpperLeg_L", "UpperLeg_R", "LowerLeg_L", "LowerLeg_R", "UpperArm_L", "UpperArm_R" }
                    .Select(name => (Bone: bones[name], Held: bones[name].localRotation)).ToArray();
                float Range(float low, float high) => low + (high - low) * (float)random.NextDouble();
                for (var frame = 0; frame < 400; frame++)
                {
                    foreach (var (bone, held) in moved)
                    {
                        bone.localRotation = held * Quaternion.Euler(Range(-70f, 70f), Range(-70f, 70f), Range(-70f, 70f));
                    }

                    var root = preview.Body.transform;
                    var jump = random.NextDouble();
                    root.position += jump < 0.05 ? new Vector3(Range(-50f, 50f), 0f, Range(-50f, 50f)) : new Vector3(Range(-0.1f, 0.1f), 0f, Range(-0.1f, 0.1f));
                    root.rotation *= Quaternion.Euler(0f, jump > 0.95 ? Range(-180f, 180f) : Range(-20f, 20f), 0f);
                    var dt = random.NextDouble() < 0.05 ? 0f : Range(0.001f, 0.25f);
                    preview.Step(Mathf.Max(dt, 1e-4f), CameraOffset);
                    foreach (var chain in ClothChains.All)
                    {
                        var anchor = bones[chain.Parent].position;
                        var reach = chain.Bones.Count * 0.35f + 0.2f;
                        foreach (var particle in preview.Solver.ParticlesOf(chain))
                        {
                            Assert.That(float.IsNaN(particle.x) || float.IsNaN(particle.y) || float.IsNaN(particle.z), Is.False, chain.Bones[0]);
                            Assert.That(Vector3.Distance(particle, anchor), Is.LessThan(reach), chain.Bones[0]);
                        }
                    }
                }
            }
        }

        // Taking the springs away and giving them back fades them out and in, the springs starting
        // from the target: in no frame does the cloth's swing on its target change a centimeter more
        // than the swing of a body that keeps its springs, beyond the fade's own share of the swing.
        [Test]
        public void ChangingTheLevelOfDetailDoesNotJoltTheCloth()
        {
            using (var kept = Preview(new[] { "WoolRobe", "WoolCloak" }))
            using (var changed = Preview(new[] { "WoolRobe", "WoolCloak" }))
            {
                var motion = ClothStressMotion.Named("Jog");
                kept.Play(motion);
                changed.Play(motion);
                List<Vector3> keptBefore = null;
                List<Vector3> changedBefore = null;
                var fadeFrames = Mathf.CeilToInt(ClothSprings.FadeSeconds / Step) + 2;
                var keptLargest = 0f;
                for (var frame = 0; frame < 240; frame++)
                {
                    changed.Springs = frame < 100 || frame >= 160;
                    kept.Step(Step, CameraOffset);
                    changed.Step(Step, CameraOffset);
                    var keptNow = Swing(kept);
                    var changedNow = Swing(changed);
                    if (keptBefore != null)
                    {
                        var keptChange = keptNow.Select((offset, index) => Vector3.Distance(offset, keptBefore[index])).Max();
                        var changedChange = changedNow.Select((offset, index) => Vector3.Distance(offset, changedBefore[index])).Max();
                        keptLargest = Mathf.Max(keptLargest, keptChange);
                        if (frame >= 100 && frame < 100 + fadeFrames)
                        {
                            // Fading out, the body's springs swing on as the kept body's do, faded.
                            var fade = keptNow.Max(offset => offset.magnitude) * Step / ClothSprings.FadeSeconds;
                            Assert.That(changedChange, Is.LessThan(keptChange + fade + 0.01f), "frame " + frame);
                        }
                        else if (frame >= 160 && frame < 160 + fadeFrames)
                        {
                            // Fading in, they start from the target and swing no harder than springs do.
                            Assert.That(changedChange, Is.LessThan(keptLargest + 0.01f), "frame " + frame);
                        }
                    }

                    keptBefore = keptNow;
                    changedBefore = changedNow;
                }

                // Faded out, the body shows its target pose.
                Assert.That(changed.Solver.SpringWeight, Is.EqualTo(1f));
            }
        }

        private static WardrobePreview Preview(string[] garments)
        {
            var preview = new WardrobePreview(WardrobePreview.Bodies().Single(body => body.Name == "Human_Male"));
            preview.Wear(garments);
            return preview;
        }

        // Puts a bone back to its bind pose under its parent, from its renderer's bind poses.
        private static void Rest(Transform bone, WardrobePreview preview)
        {
            foreach (var part in preview.Body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var index = System.Array.IndexOf(part.bones, bone);
                if (index < 0 || index >= part.sharedMesh.bindposes.Length)
                {
                    continue;
                }

                var parentIndex = bone.parent != null ? System.Array.IndexOf(part.bones, bone.parent) : -1;
                if (parentIndex < 0 || parentIndex >= part.sharedMesh.bindposes.Length)
                {
                    return;
                }

                var local = part.sharedMesh.bindposes[parentIndex] * part.sharedMesh.bindposes[index].inverse;
                bone.localPosition = local.GetPosition();
                bone.localRotation = local.rotation;
                return;
            }
        }

        // Every particle of every chain in the frame of the bone its chain hangs from.
        private static List<Vector3> Local(WardrobePreview preview)
        {
            var bones = preview.Body.GetComponentsInChildren<Transform>(true).GroupBy(bone => bone.name)
                .ToDictionary(group => group.Key, group => group.First());
            var found = new List<Vector3>();
            foreach (var chain in ClothChains.All)
            {
                found.AddRange(preview.Solver.ParticlesOf(chain).Select(particle => bones[chain.Parent].InverseTransformPoint(particle)));
            }

            return found;
        }

        // How far every particle lies from its target pose, in the frame of the bone its chain hangs
        // from.
        private static List<Vector3> Swing(WardrobePreview preview)
        {
            var bones = preview.Body.GetComponentsInChildren<Transform>(true).GroupBy(bone => bone.name)
                .ToDictionary(group => group.Key, group => group.First());
            var found = new List<Vector3>();
            foreach (var chain in ClothChains.All)
            {
                var targets = preview.Solver.TargetsOf(chain);
                found.AddRange(preview.Solver.ParticlesOf(chain).Select((particle, index) =>
                    bones[chain.Parent].InverseTransformVector(particle - targets[index])));
            }

            return found;
        }

        // How far apart two bodies' chains lie in the frames of their bones, at most.
        private static float Apart(WardrobePreview first, WardrobePreview second)
        {
            var a = Local(first);
            var b = Local(second);
            return a.Count == b.Count ? a.Select((place, index) => Vector3.Distance(place, b[index])).DefaultIfEmpty(0f).Max() : float.PositiveInfinity;
        }
    }
}
