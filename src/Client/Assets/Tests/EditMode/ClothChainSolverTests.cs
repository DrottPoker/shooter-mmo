using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The cloth chains of the Humanoid skeleton and the solver that moves them on the body, posed
    // by its Animator step by step; see docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class ClothChainSolverTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";
        private const float Step = 1f / 60f;

        private static readonly int SpeedId = Animator.StringToHash(HumanLocomotion.SpeedParameter);
        private static readonly int MovingId = Animator.StringToHash(HumanLocomotion.MovingParameter);
        private static readonly int RateId = Animator.StringToHash(HumanLocomotion.RateParameter);

        private GameObject body;

        [TearDown]
        public void TearDown()
        {
            if (body != null)
            {
                Object.DestroyImmediate(body);
            }
        }

        [Test]
        public void TheChainsAreTheClothChainsOfTheSkeleton()
        {
            var skeleton = ArtAssetManifests.LoadSkeleton("Humanoid");
            var chainBones = skeleton.bones.Where(bone => !string.IsNullOrEmpty(bone.chain)).Select(bone => bone.name);
            Assert.That(chainBones, Is.EquivalentTo(ClothChains.All.SelectMany(chain => chain.Bones)));
            foreach (var chain in ClothChains.All)
            {
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
            }));
            Assert.That(radii["UpperLeg_R"], Is.InRange(0.04f, 0.12f));
            Assert.That(radii["LowerLeg_R"], Is.InRange(0.03f, 0.08f));
            Assert.That(radii["UpperLeg_L"], Is.EqualTo(radii["UpperLeg_R"]).Within(0.002f));
            Assert.That(radii["Head"], Is.InRange(0.06f, 0.15f));
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
                deepest = Mathf.Max(deepest, DeepestJoint(solver, ClothChainKind.Skirt));
                if (step >= 60)
                {
                    var forward = body.transform.InverseTransformPoint(front.position).z;
                    nearest = Mathf.Min(nearest, forward);
                    farthest = Mathf.Max(farthest, forward);
                }
            }

            Assert.That(solver.CurrentDetail, Is.EqualTo(ClothChainSolver.Detail.Springs));
            // No joint of a chain lies more than 5 mm inside the body.
            Assert.That(deepest, Is.LessThan(0.005f));
            // The front of the robe swings with the legs.
            Assert.That(farthest - nearest, Is.GreaterThan(0.08f));
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

                var before = tip.position;
                Run(animator, solver, 30);
                // Breathing still moves the hips a little.
                Assert.That(Vector3.Distance(before, tip.position) / (30 * Step), Is.LessThan(0.05f), chain.Bones[0]);
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
        public void FartherAwayTheChainsFollowAndFarAwayTheyRest()
        {
            var (animator, solver) = Dress(ClothChainKind.Skirt);
            var rest = ClothChains.All.SelectMany(chain => chain.Bones).ToDictionary(bone => bone, bone => Bone(bone).localRotation);
            Move(animator, HumanLocomotion.JogSpeed);
            animator.Update(Step);
            solver.Simulate(Step, body.transform.position + Vector3.forward * (ClothChainSolver.NearDistance + 10f));
            Assert.That(solver.CurrentDetail, Is.EqualTo(ClothChainSolver.Detail.Follow));
            animator.Update(Step);
            solver.Simulate(Step, body.transform.position + Vector3.forward * (ClothChainSolver.FarDistance + 10f));
            Assert.That(solver.CurrentDetail, Is.EqualTo(ClothChainSolver.Detail.Rest));
            foreach (var pair in rest)
            {
                Assert.That(Quaternion.Angle(Bone(pair.Key).localRotation, pair.Value), Is.LessThan(0.01f), pair.Key);
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
            Assert.That(solver.CurrentDetail, Is.EqualTo(ClothChainSolver.Detail.Rest));
            foreach (var pair in rest)
            {
                Assert.That(Bone(pair.Key).localRotation, Is.EqualTo(pair.Value), pair.Key);
            }

            solver.Activate(ClothChainKind.Hood, ClothProfile.Hood);
            Assert.That(solver.IsActive, Is.True);
            solver.Deactivate(ClothChainKind.Hood);
            Assert.That(solver.IsActive, Is.False);
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

        private Transform Bone(string name)
        {
            return body.GetComponentsInChildren<Transform>(true).First(transform => transform.name == name);
        }

        // How far the deepest joint of the chains of a kind, where the bones really are after the
        // solver turned them, lies inside a capsule.
        private float DeepestJoint(ClothChainSolver solver, ClothChainKind kind)
        {
            var deepest = float.NegativeInfinity;
            foreach (var bone in ClothChains.All.Where(chain => chain.Kind == kind).SelectMany(chain => chain.Bones.Skip(1)))
            {
                var position = Bone(bone).position;
                foreach (var capsule in solver.Capsules)
                {
                    deepest = Mathf.Max(deepest, capsule.Radius - capsule.DistanceTo(position));
                }
            }

            return deepest;
        }
    }
}
