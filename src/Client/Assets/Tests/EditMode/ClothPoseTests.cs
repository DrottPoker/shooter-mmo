using System.Linq;
using Mmorpg.Client.Cloth;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The target pose of the cloth: its constants are the cloth section of the Humanoid skeleton,
    // the fixtures that the art pipeline's cloth_core.py is tested against lay as they were written
    // and are the bodies of today, and the world's Burst jobs lay what ClothPose lays. See
    // docs/ART_PIPELINE.md.
    public sealed class ClothPoseTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";
        private const string RobePath = "Assets/Art/Models/Garments/Robe_Male.fbx";
        private const string CloakPath = "Assets/Art/Models/Garments/Cloak_Male.fbx";
        // The same arithmetic on the same data, compiled by Burst or not.
        private const float Exact = 1e-5f;

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
        public void TheConstantsAreTheSkeletonsClothSection()
        {
            var cloth = ArtAssetManifests.LoadSkeleton("Humanoid").cloth;
            Assert.That(cloth.capsules.Select(capsule => (capsule.bone, string.IsNullOrEmpty(capsule.end) ? null : capsule.end)),
                Is.EqualTo(ClothChainSolver.CapsuleBones.Select(capsule => (capsule.Bone, capsule.End))));
            Assert.That(cloth.capsules.Select(capsule => capsule.role),
                Is.EqualTo(ClothChainSolver.CapsuleBones.Select(capsule => ClothChainSolver.Capsule.RoleOf(capsule.Bone).ToString().ToLowerInvariant())));
            Assert.That(cloth.directions, Is.EqualTo(ClothChainSolver.Capsule.Directions));
            Assert.That(cloth.groovePasses, Is.EqualTo(ClothChainSolver.GroovePasses));
            Assert.That(cloth.fallbackRadius, Is.EqualTo(ClothChainSolver.FallbackRadius));
            Assert.That(cloth.placeSpan, Is.EqualTo(ClothChainSolver.PlaceSpan));
            Assert.That(cloth.followReach, Is.EqualTo(ClothPose.FollowReach));
            Assert.That(cloth.layerGap, Is.EqualTo(ClothPose.LayerGap));
            Assert.That(cloth.samples, Is.EqualTo(ClothPose.Samples));
            Assert.That(cloth.maxSwingDegrees, Is.EqualTo(ClothPose.MaxSwingDegrees));
            Assert.That(cloth.swingFadeDegrees, Is.EqualTo(ClothPose.SwingFadeDegrees));
            Assert.That(cloth.advanceSteps, Is.EqualTo(ClothPose.AdvanceSteps));
            Assert.That(cloth.advanceSafety, Is.EqualTo(ClothPose.AdvanceSafety));
            Assert.That(cloth.exitAlong, Is.EqualTo(ClothPose.ExitAlong));
            Assert.That(cloth.contactBand, Is.EqualTo(ClothPose.ContactBand));
            Assert.That(cloth.armPush, Is.EqualTo(ClothPose.ArmPush));
            Assert.That(cloth.straightBlendDot, Is.EqualTo(ClothMath.StraightBlendDot));

            // The kinds in the order their cloth lies on the body, a cloak over a robe's skirt.
            Assert.That(cloth.kinds.Select(kind => kind.kind), Is.EqualTo(new[] { "skirt", "cloak", "hood" }));
            Assert.That(cloth.kinds.Select(kind => kind.over ?? string.Empty), Is.EqualTo(new[] { string.Empty, "skirt", string.Empty }));
            Assert.That(cloth.kinds.Select(kind => kind.frame ?? string.Empty), Is.EqualTo(new[] { string.Empty, string.Empty, "Head" }));
            var chains = cloth.kinds.SelectMany(kind => kind.chains.SelectMany(chain => chain.sided
                    ? new[] { (kind, chain.name, suffix: "_L", angle: -chain.angle), (kind, chain.name, suffix: "_R", angle: chain.angle) }
                    : new[] { (kind, chain.name, suffix: string.Empty, angle: chain.angle) }))
                .ToArray();
            Assert.That(chains.Select(chain => chain.name + "1" + chain.suffix), Is.EquivalentTo(ClothChains.All.Select(chain => chain.Bones[0])));
            foreach (var (kind, name, suffix, angle) in chains)
            {
                var chain = ClothChains.All.Single(candidate => candidate.Bones[0] == name + "1" + suffix);
                var chainKind = (ClothChainKind)System.Enum.Parse(typeof(ClothChainKind), kind.kind, true);
                Assert.That(chain.Kind, Is.EqualTo(chainKind), chain.Bones[0]);
                Assert.That(chain.Parent, Is.EqualTo(kind.parent), chain.Bones[0]);
                Assert.That(chain.Bones.Count, Is.EqualTo(kind.bones), chain.Bones[0]);
                Assert.That(chain.Angle, Is.EqualTo(angle), chain.Bones[0]);
                var beside = kind.beside.Select(part => (LegPart)System.Enum.Parse(typeof(LegPart), part, true)).ToArray();
                Assert.That(chain.Beside, Is.EqualTo(beside), chain.Bones[0]);
                Assert.That(ClothProfile.For(chainKind).Margin, Is.EqualTo(kind.margin), kind.kind);
                Assert.That(ClothProfile.For(chainKind).Slack, Is.EqualTo(kind.slack), kind.kind);
                var collides = kind.collides.Aggregate(ClothRoles.None,
                    (roles, role) => roles | (ClothRoles)System.Enum.Parse(typeof(ClothRoles), role, true));
                Assert.That(ClothChainSolver.CollidesWith(chainKind), Is.EqualTo(collides), kind.kind);
            }
        }

        [Test]
        public void TheFixturesLayAsTheyWereWritten()
        {
            var fixture = ClothKinematicFixtures.Read();
            Assert.That(fixture.version, Is.EqualTo(ClothKinematicFixture.Version));
            Assert.That(fixture.cases, Is.Not.Empty);
            foreach (var captured in fixture.cases)
            {
                var laid = captured.Lay();
                for (var chain = 0; chain < laid.Length; chain++)
                {
                    AssertNear(laid[chain], captured.chains[chain].expected, Exact, captured.name + " " + captured.chains[chain].root);
                }
            }
        }

        [Test]
        public void TheFixturesAreTheBodiesOfToday()
        {
            var written = ClothKinematicFixtures.Read();
            var captured = ClothKinematicFixtures.Capture();
            const string Again = "; run " + ClothKinematicFixtures.MenuPath + " and commit the fixtures";
            Assert.That(written.cases.Select(each => each.name), Is.EqualTo(captured.cases.Select(each => each.name)), "cases" + Again);
            for (var index = 0; index < captured.cases.Count; index++)
            {
                var now = captured.cases[index];
                var then = written.cases[index];
                Assert.That(then.chains.Select(chain => chain.root), Is.EqualTo(now.chains.Select(chain => chain.root)), now.name + Again);
                AssertNear(then.capsules.Select(capsule => capsule.a).ToArray(), now.capsules.Select(capsule => capsule.a).ToArray(), Exact,
                    now.name + " capsules" + Again);
                for (var chain = 0; chain < now.chains.Count; chain++)
                {
                    var where = now.name + " " + now.chains[chain].root + Again;
                    AssertNear(then.chains[chain].hanging, now.chains[chain].hanging, Exact, where);
                    AssertNear(then.chains[chain].bindHanging, now.chains[chain].bindHanging, Exact, where);
                    AssertNear(then.chains[chain].expected, now.chains[chain].expected, Exact, where);
                }
            }

            // The cases hold the body's capsules, every kind of cloth, a cloak over a robe, and both bodies.
            Assert.That(captured.cases.SelectMany(each => each.capsules.Concat(each.bindCapsules)),
                Has.All.Matches<ClothKinematicCapsule>(capsule => capsule.radii.Length > 0 && capsule.role != "none"));
            Assert.That(captured.cases.SelectMany(each => each.chains).Select(chain => chain.kind).Distinct(),
                Is.EquivalentTo(new[] { "skirt", "cloak", "hood" }));
            Assert.That(captured.cases.Count(each => each.chains.Any(chain => chain.kind == "skirt" && chain.reachKnown)
                && each.chains.Any(chain => chain.kind == "cloak" && chain.reachKnown)), Is.GreaterThan(10));
            Assert.That(captured.cases.Select(each => each.name.Split('/')[0]).Distinct(), Is.EquivalentTo(new[] { "male", "female" }));
        }

        [Test]
        public void TheWorldsJobsLayWhatClothPoseLays()
        {
            var solver = Dressed(0.37f);
            solver.Simulate(1f / 60f, ClothDetail.Pose);
            var captured = solver.CaptureKinematics("jobs");
            var laid = captured.Lay();
            for (var chain = 0; chain < laid.Length; chain++)
            {
                AssertNear(laid[chain], captured.chains[chain].expected, Exact, captured.chains[chain].root);
                AssertNear(solver.ParticlesOf(ClothChains.All.Single(each => each.Bones[0] == captured.chains[chain].root)).ToArray(),
                    captured.chains[chain].expected, Exact, captured.chains[chain].root);
            }
        }

        [Test]
        public void ThePoseHasNoMemory()
        {
            // The same pose lays the chains alike whatever came before it.
            var solver = Dressed(0.1f);
            var animator = body.GetComponent<Animator>();
            solver.Simulate(1f / 60f, ClothDetail.Pose);
            var before = ClothChains.All.Where(chain => chain.Kind != ClothChainKind.Hood).Select(chain => solver.ParticlesOf(chain).ToArray()).ToArray();
            for (var step = 0; step < 30; step++)
            {
                animator.Update(0.05f);
                solver.Simulate(0.05f, ClothDetail.Pose);
            }

            animator.Rebind();
            Jog(animator);
            animator.Update(0.1f);
            solver.Simulate(1f / 60f, ClothDetail.Pose);
            var after = ClothChains.All.Where(chain => chain.Kind != ClothChainKind.Hood).Select(chain => solver.ParticlesOf(chain).ToArray()).ToArray();
            for (var chain = 0; chain < before.Length; chain++)
            {
                AssertNear(after[chain], before[chain], Exact, "chain " + chain);
            }
        }

        // The male body jogging at a time of its clip, in the robe and the cloak.
        private ClothChainSolver Dressed(float seconds)
        {
            body = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath));
            body.hideFlags = HideFlags.HideAndDontSave;
            var animator = body.GetComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            Jog(animator);
            animator.Update(seconds);
            var solver = body.GetComponent<ClothChainSolver>();
            solver.Activate(ClothChainKind.Skirt, ClothProfile.Robe, AssetDatabase.LoadAssetAtPath<GameObject>(RobePath));
            solver.Activate(ClothChainKind.Cloak, ClothProfile.Cloak, AssetDatabase.LoadAssetAtPath<GameObject>(CloakPath));
            return solver;
        }

        private static void Jog(Animator animator)
        {
            animator.SetBool(Animator.StringToHash(HumanLocomotion.MovingParameter), true);
            animator.SetFloat(Animator.StringToHash(HumanLocomotion.SpeedParameter), HumanLocomotion.JogSpeed);
            animator.SetFloat(Animator.StringToHash(HumanLocomotion.RateParameter), 1f);
        }

        private static void AssertNear(Vector3[] actual, Vector3[] expected, float tolerance, string message)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), message);
            for (var index = 0; index < actual.Length; index++)
            {
                Assert.That(Vector3.Distance(actual[index], expected[index]), Is.LessThanOrEqualTo(tolerance), message + " particle " + index);
            }
        }
    }
}
