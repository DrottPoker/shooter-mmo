using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Cloth;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The cloth chains of the Humanoid skeleton and the solver that lays them on the body in its
    // target pose and swings them with springs on it, posed by its Animator step by step; see
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class ClothChainSolverTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";
        private const string RobePath = "Assets/Art/Models/Garments/Robe_Male.fbx";
        private const string CloakPath = "Assets/Art/Models/Garments/Cloak_Male.fbx";
        private const float Step = 1f / 60f;

        private static readonly int SpeedId = Animator.StringToHash(HumanLocomotion.SpeedParameter);
        private static readonly int MovingId = Animator.StringToHash(HumanLocomotion.MovingParameter);
        private static readonly int RateId = Animator.StringToHash(HumanLocomotion.RateParameter);

        private readonly List<GameObject> others = new List<GameObject>();
        private GameObject body;

        [TearDown]
        public void TearDown()
        {
            if (body != null)
            {
                Object.DestroyImmediate(body);
            }

            foreach (var other in others)
            {
                Object.DestroyImmediate(other);
            }

            others.Clear();
            ClothChainSolver.SharesMeasuresOutOfPlay = false;
            ClothChainSolver.ForgetSharedMeasures();
        }

        [Test]
        public void TheChainsAreTheClothChainsOfTheSkeleton()
        {
            var skeleton = ArtAssetManifests.LoadSkeleton("Humanoid");
            var chainBones = skeleton.bones.Where(bone => !string.IsNullOrEmpty(bone.chain)).Select(bone => bone.name);
            Assert.That(chainBones, Is.EquivalentTo(ClothChains.All.SelectMany(chain => chain.Bones)));
            foreach (var chain in ClothChains.All)
            {
                // Skirt and cloak chains name the part of the legs beside each of their bones.
                Assert.That(chain.Beside.Count, Is.EqualTo(chain.Kind == ClothChainKind.Hood ? 0 : chain.Bones.Count), chain.Bones[0]);
                for (var index = 0; index < chain.Bones.Count; index++)
                {
                    var bone = skeleton.bones.Single(candidate => candidate.name == chain.Bones[index]);
                    Assert.That(bone.chain, Is.EqualTo(chain.Kind.ToString().ToLowerInvariant()), bone.name);
                    Assert.That(bone.parent, Is.EqualTo(index == 0 ? chain.Parent : chain.Bones[index - 1]), bone.name);
                    Assert.That(bone.deform, Is.True, bone.name);
                    Assert.That(bone.human, Is.Empty, bone.name);
                }
            }
        }

        [Test]
        public void TheBodyHasEveryChainAndIsNeverWeightedToIt()
        {
            SpawnBody();
            var transforms = body.GetComponentsInChildren<Transform>(true)
                .GroupBy(transform => transform.name)
                .ToDictionary(group => group.Key, group => group.First());
            var chainBones = new HashSet<string>(ClothChains.All.SelectMany(chain => chain.Bones));
            foreach (var chain in ClothChains.All)
            {
                for (var index = 0; index < chain.Bones.Count; index++)
                {
                    Assert.That(transforms.TryGetValue(chain.Bones[index], out var bone), Is.True, chain.Bones[index]);
                    Assert.That(bone.parent.name, Is.EqualTo(index == 0 ? chain.Parent : chain.Bones[index - 1]), bone.name);
                }
            }

            foreach (var renderer in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var weighted = new HashSet<int>(renderer.sharedMesh.GetAllBoneWeights()
                    .Where(weight => weight.weight > 0f)
                    .Select(weight => weight.boneIndex));
                foreach (var index in weighted)
                {
                    Assert.That(chainBones.Contains(renderer.bones[index].name), Is.False, renderer.name + " " + renderer.bones[index].name);
                }
            }
        }

        [Test]
        public void TheCapsulesFitTheBody()
        {
            var (_, solver) = Dress(ClothChainKind.Skirt);
            var radii = solver.Capsules.ToDictionary(capsule => capsule.BoneName, capsule => capsule.Radius);
            Assert.That(radii.Keys, Is.EquivalentTo(new[]
            {
                "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head", "UpperLeg_L", "LowerLeg_L", "UpperLeg_R", "LowerLeg_R",
                "Foot_L", "Foot_R", "UpperArm_L", "LowerArm_L", "UpperArm_R", "LowerArm_R",
            }));
            Assert.That(radii["UpperLeg_R"], Is.InRange(0.04f, 0.14f));
            Assert.That(radii["Foot_R"], Is.InRange(0.03f, 0.1f));
            Assert.That(radii["UpperArm_R"], Is.InRange(0.04f, 0.12f));
            Assert.That(radii["LowerArm_R"], Is.InRange(0.02f, 0.06f));
            Assert.That(radii["UpperArm_L"], Is.EqualTo(radii["UpperArm_R"]).Within(0.002f));
            Assert.That(radii["LowerLeg_R"], Is.InRange(0.03f, 0.08f));
            Assert.That(radii["UpperLeg_L"], Is.EqualTo(radii["UpperLeg_R"]).Within(0.002f));
            Assert.That(radii["Head"], Is.InRange(0.06f, 0.15f));
            // The chest is deep in front of the spine and shallow behind it, so a cloak lies on the back.
            var chest = solver.Capsules.Single(capsule => capsule.BoneName == "Chest");
            Assert.That(chest.RadiusToward(-body.transform.forward), Is.LessThan(0.08f));
            Assert.That(chest.RadiusToward(body.transform.forward), Is.GreaterThan(2f * chest.RadiusToward(-body.transform.forward)));
        }

        [TestCase(ClothChainKind.Skirt, RobePath)]
        [TestCase(ClothChainKind.Cloak, CloakPath)]
        public void AtRestTheChainsHangAsTheGarmentsWereMade(ClothChainKind kind, string garmentPath)
        {
            SpawnBody();
            body.GetComponent<Animator>().enabled = false;
            var rest = ClothChains.All.Where(chain => chain.Kind == kind).SelectMany(chain => chain.Bones)
                .ToDictionary(bone => bone, bone => Bone(bone).localRotation);
            var garment = AssetDatabase.LoadAssetAtPath<GameObject>(garmentPath);
            Assert.That(garment, Is.Not.Null, garmentPath);
            var solver = body.GetComponent<ClothChainSolver>();
            solver.Activate(kind, ClothProfile.For(kind), garment);
            for (var step = 0; step < 10; step++)
            {
                solver.Simulate(Step, body.transform.position + Vector3.forward * 3f);
            }

            // Every sample of the cloth keeps no more margin from the body than it has in the bind
            // pose, and gravity counts against the bind pose, so a chain keeps the place it has there.
            Assert.That(solver.CurrentDetail, Is.EqualTo(ClothDetail.Spring));
            foreach (var pair in rest)
            {
                Assert.That(Quaternion.Angle(Bone(pair.Key).localRotation, pair.Value), Is.LessThan(1f), pair.Key);
            }
        }

        [Test]
        public void ARobeFollowsTheLegsAndStaysOutsideThemWhileJogging()
        {
            var (animator, solver) = Dress(ClothChainKind.Skirt);
            Move(animator, HumanLocomotion.JogSpeed);
            var front = Bone(ClothChains.All.First(chain => chain.Bones[0] == "SkirtFront1").Bones[3]);
            var deepest = float.NegativeInfinity;
            float nearest = float.MaxValue, farthest = float.MinValue;
            for (var step = 0; step < 240; step++)
            {
                animator.Update(Step);
                solver.Simulate(Step, body.transform.position);
                deepest = Mathf.Max(deepest, DeepestInLegs(solver, ClothChainKind.Skirt));
                if (step >= 60)
                {
                    var forward = body.transform.InverseTransformPoint(front.position).z;
                    nearest = Mathf.Min(nearest, forward);
                    farthest = Mathf.Max(farthest, forward);
                }
            }

            Assert.That(solver.CurrentDetail, Is.EqualTo(ClothDetail.Spring));
            // No joint of a chain, nor the cloth between it and its neighbours, lies in the legs.
            Assert.That(deepest, Is.LessThan(0.001f));
            // The front of the robe swings with the legs.
            Assert.That(farthest - nearest, Is.GreaterThan(0.08f));
        }

        [Test]
        public void ARobeDrapesOverThighsRaisedAsInSitting()
        {
            var (animator, solver) = Dress(ClothChainKind.Skirt);
            animator.enabled = false;
            var forward = body.transform.forward;
            foreach (var side in new[] { "_L", "_R" })
            {
                // The thigh turns forward until it is nearly level, and the shin hangs down from the knee.
                var thigh = Bone("UpperLeg" + side);
                var knee = Bone("LowerLeg" + side);
                var along = (knee.position - thigh.position).normalized;
                thigh.rotation = Quaternion.FromToRotation(along, Vector3.Slerp(along, forward, 0.85f)) * thigh.rotation;
                var foot = Bone("Foot" + side);
                knee.rotation = Quaternion.FromToRotation(foot.position - knee.position, Vector3.down) * knee.rotation;
            }

            Run(solver, 180);
            var hips = Bone("Hips").position;
            var knees = Mathf.Max(Vector3.Dot(Bone("LowerLeg_L").position - hips, forward),
                Vector3.Dot(Bone("LowerLeg_R").position - hips, forward));
            // The front of the robe reaches over the thighs to the knees and hangs from there.
            var front = ClothChains.All.First(chain => chain.Bones[0] == "SkirtFront1");
            Assert.That(Vector3.Dot(Bone(front.Bones[2]).position - hips, forward), Is.GreaterThan(knees - 0.06f));
            Assert.That(Bone(front.Bones[3]).position.y, Is.LessThan(Bone(front.Bones[2]).position.y));
            Assert.That(DeepestInLegs(solver, ClothChainKind.Skirt), Is.LessThan(0.001f));
        }

        [Test]
        public void ACloakIsPushedBackByALegThatKicksBack()
        {
            var (animator, solver) = Dress(ClothChainKind.Cloak);
            animator.enabled = false;
            var forward = body.transform.forward;
            var right = ClothChains.All.Where(chain => chain.Kind == ClothChainKind.Cloak && chain.Angle >= 0f).ToArray();
            Run(solver, 180);
            var hips = Bone("Hips").position;
            var hanging = right.ToDictionary(chain => chain, chain => Vector3.Dot(solver.ParticlesOf(chain).Last() - hips, -forward));
            // The right thigh swings back and the shin folds up behind it, as in a jog's back kick.
            var thigh = Bone("UpperLeg_R");
            var knee = Bone("LowerLeg_R");
            var along = (knee.position - thigh.position).normalized;
            thigh.rotation = Quaternion.FromToRotation(along, Vector3.Slerp(along, -forward, 0.35f)) * thigh.rotation;
            var foot = Bone("Foot_R");
            var shin = (foot.position - knee.position).normalized;
            knee.rotation = Quaternion.FromToRotation(shin, Vector3.Slerp(-forward, Vector3.down, 0.3f)) * knee.rotation;
            Run(solver, 180);
            var kneeAt = Bone("LowerLeg_R").position;
            var footAt = Bone("Foot_R").position;
            foreach (var chain in right)
            {
                var hem = solver.ParticlesOf(chain).Last();
                // The shin pushes the hem back.
                Assert.That(Vector3.Dot(hem - hips, -forward), Is.GreaterThan(hanging[chain] + 0.05f), chain.Bones[0]);
                // The hems behind the leg lie over the raised shin; the outer one, at the leg's side,
                // hangs beside it.
                if (chain.Angle > 140f)
                {
                    var onShin = kneeAt + Vector3.Project(hem - kneeAt, footAt - kneeAt);
                    Assert.That(hem.y, Is.GreaterThan(onShin.y), chain.Bones[0]);
                }
            }

            // No hem hangs through the leg.
            Assert.That(DeepestInLegs(solver, ClothChainKind.Cloak), Is.LessThan(0.001f));
        }

        [Test]
        public void TheChainsSettleWhenTheBodyStandsStill()
        {
            var (animator, solver) = Dress(ClothChainKind.Skirt, ClothChainKind.Cloak);
            Move(animator, HumanLocomotion.JogSpeed);
            Run(animator, solver, 120);
            Move(animator, 0f);
            Run(animator, solver, 300);
            foreach (var chain in ClothChains.All.Where(chain => chain.Kind != ClothChainKind.Hood))
            {
                var root = Bone(chain.Bones[0]);
                var tip = Bone(chain.Bones[chain.Bones.Count - 1]);
                var reach = 0f;
                for (var index = 1; index < chain.Bones.Count; index++)
                {
                    reach += Vector3.Distance(Bone(chain.Bones[index - 1]).position, Bone(chain.Bones[index]).position);
                }

                // The tip rests on the bone the chain hangs from, which breathing still moves, and
                // the farther it hangs, the more: so it is measured in that bone's frame. Where the
                // legs touch the cloth it moves as they shift the weight, and no faster.
                var anchor = root.parent;
                var legs = new[] { "LowerLeg_L", "LowerLeg_R", "Foot_L", "Foot_R" }.Select(Bone).ToArray();
                var before = anchor.InverseTransformPoint(tip.position);
                var legsBefore = legs.Select(leg => anchor.InverseTransformPoint(leg.position)).ToArray();
                Run(animator, solver, 30);
                var legSpeed = legs.Select((leg, index) => Vector3.Distance(legsBefore[index], anchor.InverseTransformPoint(leg.position))).Max()
                    / (30 * Step);
                Assert.That(Vector3.Distance(before, anchor.InverseTransformPoint(tip.position)) / (30 * Step), Is.LessThan(legSpeed + 0.01f),
                    chain.Bones[0]);
                // The chain hangs down from its root, not out to the side.
                Assert.That(root.position.y - tip.position.y, Is.GreaterThan(0.6f * reach), chain.Bones[0]);
            }
        }

        [Test]
        public void ATeleportPutsTheChainsBackOnTheBody()
        {
            var (animator, solver) = Dress(ClothChainKind.Skirt, ClothChainKind.Cloak, ClothChainKind.Hood);
            Move(animator, HumanLocomotion.WalkSpeed);
            Run(animator, solver, 30);
            body.transform.position += new Vector3(100f, 0f, 0f);
            Run(animator, solver, 1);
            var hips = Bone("Hips").position;
            foreach (var bone in ClothChains.All.SelectMany(chain => chain.Bones))
            {
                Assert.That(Vector3.Distance(Bone(bone).position, hips), Is.LessThan(2f), bone);
            }
        }

        [Test]
        public void WithinTheFarDistanceTheChainsAreLaidAndBeyondTheyRest()
        {
            var (animator, solver) = Dress(ClothChainKind.Skirt);
            var rest = ClothChains.All.SelectMany(chain => chain.Bones).ToDictionary(bone => bone, bone => Bone(bone).localRotation);
            Move(animator, HumanLocomotion.JogSpeed);
            animator.Update(Step);
            solver.Simulate(Step, body.transform.position + Vector3.forward * (ClothChainSolver.FarDistance - 10f));
            Assert.That(solver.CurrentDetail, Is.EqualTo(ClothDetail.Spring));
            animator.Update(Step);
            solver.Simulate(Step, body.transform.position + Vector3.forward * (ClothChainSolver.FarDistance + 10f));
            Assert.That(solver.CurrentDetail, Is.EqualTo(ClothDetail.Rest));
            foreach (var pair in rest)
            {
                Assert.That(Quaternion.Angle(Bone(pair.Key).localRotation, pair.Value), Is.LessThan(0.01f), pair.Key);
            }
        }

        [Test]
        public void OutOfViewTheChainsKeepTheirPoseAndStartAgainWhenSeen()
        {
            var (animator, solver) = Dress(ClothChainKind.Skirt);
            Move(animator, HumanLocomotion.JogSpeed);
            Run(animator, solver, 60);
            var pose = ClothChains.All.SelectMany(chain => chain.Bones).ToDictionary(bone => bone, bone => Bone(bone).localRotation);
            animator.Update(Step);
            solver.Simulate(Step, ClothDetail.Hidden);
            Assert.That(solver.CurrentDetail, Is.EqualTo(ClothDetail.Hidden));
            foreach (var pair in pose)
            {
                Assert.That(Bone(pair.Key).localRotation, Is.EqualTo(pair.Value), pair.Key);
            }

            // The body moved far while out of view; seen again, its chains hang from it at once.
            Move(animator, 0f);
            body.transform.position += new Vector3(1.5f, 0f, 0f);
            animator.Update(Step);
            solver.Simulate(Step, ClothDetail.Pose);
            foreach (var chain in ClothChains.All.Where(chain => chain.Kind == ClothChainKind.Skirt))
            {
                var root = Bone(chain.Bones[0]);
                var tip = Bone(chain.Bones[chain.Bones.Count - 1]);
                Assert.That(root.position.y - tip.position.y, Is.GreaterThan(0.4f), chain.Bones[0]);
            }
        }

        [Test]
        public void InPlayBodiesOfOneModelShareWhatWasMeasuredAndMoveAlike()
        {
            // A body that measured itself, as every body does out of play.
            var (aloneAnimator, alone) = Dress(ClothChainKind.Skirt, ClothChainKind.Cloak);
            ClothChainSolver.SharesMeasuresOutOfPlay = true;
            var (firstAnimator, first) = DressOther(ClothChainKind.Skirt, ClothChainKind.Cloak);
            var (secondAnimator, second) = DressOther(ClothChainKind.Skirt, ClothChainKind.Cloak);
            Assert.That(second.Capsules.Select(capsule => capsule.Radius), Is.EqualTo(alone.Capsules.Select(capsule => capsule.Radius)));

            foreach (var animator in new[] { aloneAnimator, firstAnimator, secondAnimator })
            {
                Move(animator, HumanLocomotion.JogSpeed);
            }

            for (var step = 0; step < 120; step++)
            {
                foreach (var (animator, solver) in new[] { (aloneAnimator, alone), (firstAnimator, first), (secondAnimator, second) })
                {
                    animator.Update(Step);
                    solver.Simulate(Step, ClothDetail.Pose);
                }
            }

            foreach (var chain in ClothChains.All.Where(chain => chain.Kind != ClothChainKind.Hood))
            {
                var expected = alone.ParticlesOf(chain);
                foreach (var solver in new[] { first, second })
                {
                    var particles = solver.ParticlesOf(chain);
                    for (var index = 0; index < expected.Count; index++)
                    {
                        Assert.That(Vector3.Distance(particles[index], expected[index]), Is.LessThan(1e-4f), chain.Bones[0] + " " + index);
                    }
                }
            }
        }

        // A body's capsules fit the body alone, not the garments drawn with it, so every body of a
        // model collides alike whatever it wore when its cloth went on.
        [Test]
        public void TheCapsulesFitTheBodyAloneNotTheGarmentsItWears()
        {
            var (_, bare) = Dress(ClothChainKind.Cloak);
            var expected = bare.Capsules.Select(capsule => capsule.Radius).ToArray();
            using (var preview = new WardrobePreview(WardrobePreview.Bodies().Single(candidate => candidate.Name == "Human_Male")))
            {
                preview.Wear(new[] { "WoolTrousers", "LinenLongShirt", "WoolCloak" });
                Assert.That(preview.Solver.IsActive, Is.True);
                Assert.That(preview.Solver.Capsules.Select(capsule => capsule.Radius), Is.EqualTo(expected).Within(1e-5f));
            }
        }

        [Test]
        public void ABodyWithoutClothLeavesItsChainsAlone()
        {
            SpawnBody();
            var solver = body.GetComponent<ClothChainSolver>();
            var rest = ClothChains.All.SelectMany(chain => chain.Bones).ToDictionary(bone => bone, bone => Bone(bone).localRotation);
            solver.Simulate(Step, body.transform.position);
            Assert.That(solver.IsActive, Is.False);
            Assert.That(solver.CurrentDetail, Is.EqualTo(ClothDetail.Rest));
            foreach (var pair in rest)
            {
                Assert.That(Bone(pair.Key).localRotation, Is.EqualTo(pair.Value), pair.Key);
            }

            solver.Activate(ClothChainKind.Hood, ClothProfile.Hood);
            Assert.That(solver.IsActive, Is.True);
            solver.Deactivate(ClothChainKind.Hood);
            Assert.That(solver.IsActive, Is.False);
        }

        // How far the cloth at the deepest joint or tip of the chains of a kind lies in a leg beyond
        // what the target pose lets it, as the last step laid it: the cloth lies its garment's reach
        // within each joint and keeps the profile's margin from the legs, but no more than it has in
        // the bind pose less the contact band, as docs/EQUIPMENT_VISUALS_DESIGN.md describes.
        private static float DeepestInLegs(ClothChainSolver solver, ClothChainKind kind)
        {
            var captured = solver.CaptureKinematics("legs");
            var margin = ClothProfile.For(kind).Margin;
            var deepest = float.NegativeInfinity;
            foreach (var chain in captured.chains.Where(chain => chain.kind == kind.ToString().ToLowerInvariant()))
            {
                for (var particle = 1; particle < chain.expected.Length; particle++)
                {
                    var within = chain.reachKnown && chain.hasReach[particle] ? chain.reach[particle].y : 0f;
                    for (var capsule = 0; capsule < captured.capsules.Count; capsule++)
                    {
                        if (captured.capsules[capsule].role != "leg")
                        {
                            continue;
                        }

                        var now = DepthIn(captured.capsules[capsule], chain.expected[particle], margin + within);
                        var atRest = DepthIn(captured.bindCapsules[capsule], chain.bindHanging[particle], margin + within);
                        deepest = Mathf.Max(deepest, now - Mathf.Max(atRest + ClothPose.ContactBand, 0f));
                    }
                }
            }

            return deepest;
        }

        // How far a point lies within a captured capsule and a margin.
        private static float DepthIn(ClothKinematicCapsule capsule, Vector3 point, float margin)
        {
            var radii = new NativeArray<float>(capsule.radii, Allocator.Temp);
            try
            {
                return ClothPose.Depth(capsule.Capsule(0), radii, point, margin);
            }
            finally
            {
                radii.Dispose();
            }
        }

        private void SpawnBody()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
            Assert.That(prefab, Is.Not.Null, BodyPrefabPath);
            body = Object.Instantiate(prefab);
            body.hideFlags = HideFlags.HideAndDontSave;
            // The body moves the chains of the garments it wears.
            Assert.That(body.GetComponent<ClothChainSolver>(), Is.Not.Null);
        }

        private (Animator Animator, ClothChainSolver Solver) Dress(params ClothChainKind[] kinds)
        {
            SpawnBody();
            var animator = body.GetComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            animator.Update(0f);
            var solver = body.GetComponent<ClothChainSolver>();
            foreach (var kind in kinds)
            {
                solver.Activate(kind, ClothProfile.For(kind));
            }

            Assert.That(solver.IsActive, Is.True);
            return (animator, solver);
        }

        // Another body, dressed like Dress dresses the test's own.
        private (Animator Animator, ClothChainSolver Solver) DressOther(params ClothChainKind[] kinds)
        {
            var other = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath));
            other.hideFlags = HideFlags.HideAndDontSave;
            others.Add(other);
            var animator = other.GetComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            animator.Update(0f);
            var solver = other.GetComponent<ClothChainSolver>();
            foreach (var kind in kinds)
            {
                solver.Activate(kind, ClothProfile.For(kind));
            }

            return (animator, solver);
        }

        private static void Move(Animator animator, float speed)
        {
            animator.SetBool(MovingId, speed > HumanLocomotion.MovingSpeed);
            animator.SetFloat(SpeedId, speed);
            animator.SetFloat(RateId, 1f);
        }

        private void Run(Animator animator, ClothChainSolver solver, int steps)
        {
            for (var step = 0; step < steps; step++)
            {
                animator.Update(Step);
                solver.Simulate(Step, body.transform.position);
            }
        }

        // Steps the solver on a pose set by hand, with the Animator off.
        private void Run(ClothChainSolver solver, int steps)
        {
            for (var step = 0; step < steps; step++)
            {
                solver.Simulate(Step, body.transform.position);
            }
        }

        private Transform Bone(string name)
        {
            return body.GetComponentsInChildren<Transform>(true).First(transform => transform.name == name);
        }
    }
}
