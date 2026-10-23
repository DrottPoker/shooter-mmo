using System.Linq;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The Wardrobe preview dresses the bodies, plays their clips on a moving ground, and runs the
    // cloth solver, as docs/EQUIPMENT_VISUALS_DESIGN.md describes under Review.
    public sealed class WardrobePreviewTests
    {
        private const string Jerkin = "LeatherJerkin";
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";

        [Test]
        public void TheWardrobeDressesEveryBodyInItsGarments()
        {
            var bodies = WardrobePreview.Bodies();
            Assert.That(bodies.Select(body => body.Name), Is.EquivalentTo(new[] { "Human_Female", "Human_Male" }));
            foreach (var body in bodies)
            {
                using (var preview = new WardrobePreview(body))
                {
                    var renderer = preview.Body.GetComponent<PlayerBodyAppearance>().CombinedRenderer;
                    var bare = renderer.sharedMesh;
                    preview.Wear(new[] { Jerkin });
                    Assert.That(preview.Worn.Select(garment => garment.Name), Is.EqualTo(new[] { Jerkin }), body.Name);
                    Assert.That(renderer.sharedMesh, Is.Not.SameAs(bare), body.Name);
                    Assert.That(renderer.sharedMaterials.Select(material => material.name), Does.Contain("M_Garment_" + Jerkin), body.Name);
                    preview.Wear(new string[0]);
                    Assert.That(preview.Worn, Is.Empty, body.Name);
                }
            }
        }

        [Test]
        public void AJogMovesTheBodyAtItsSpeedWithTheGroundUnderIt()
        {
            using (var preview = new WardrobePreview(WardrobePreview.Bodies()[0]))
            {
                preview.Play(WardrobePreview.Motion.Jog);
                var hips = preview.Body.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips);
                var rest = hips.localRotation;
                for (var step = 0; step < 10; step++)
                {
                    preview.Step(0.1f, preview.Body.transform.position + new Vector3(0f, 1.5f, 3f));
                }

                Assert.That(preview.Body.transform.position.z, Is.EqualTo(HumanLocomotion.JogSpeed).Within(0.001f));
                Assert.That(Quaternion.Angle(hips.localRotation, rest), Is.GreaterThan(0.5f), "the body jogs");
                var ground = preview.Scene.GetRootGameObjects().Single(root => root.name == "WardrobeGround").transform;
                Assert.That(Mathf.Abs(ground.position.z - preview.Body.transform.position.z), Is.LessThanOrEqualTo(WardrobePreview.TileSize));

                preview.Play(WardrobePreview.Motion.Idle);
                var stopped = preview.Body.transform.position;
                preview.Step(0.5f, stopped + Vector3.forward * 3f);
                Assert.That(preview.Body.transform.position, Is.EqualTo(stopped));
            }
        }

        [Test]
        public void DeathLaysTheBodyDownInTheWardrobe()
        {
            using (var preview = new WardrobePreview(WardrobePreview.Bodies()[0]))
            {
                preview.Play(WardrobePreview.Motion.Death);
                for (var step = 0; step < 30; step++)
                {
                    preview.Step(0.1f, preview.Body.transform.position + Vector3.forward * 3f);
                }

                var animator = preview.Body.GetComponent<Animator>();
                Assert.That(animator.GetCurrentAnimatorStateInfo(0).IsName(HumanLocomotion.DeathState), Is.True);
                Assert.That(preview.Speed, Is.Zero);
            }
        }

        // The CI runs Unity without graphics, so the tests cannot render; the window draws the preview.
        [Test]
        public void ClosingTheWardrobeLeavesNothingBehind()
        {
            var preview = new WardrobePreview(WardrobePreview.Bodies()[0]);
            var scene = preview.Scene;
            var body = preview.Body;
            preview.Wear(new[] { Jerkin });
            Assert.That(scene.IsValid(), Is.True);

            preview.Dispose();
            Assert.That(scene.IsValid(), Is.False);
            Assert.That(body == null, Is.True);
        }

        [Test]
        public void TheBodyPrefabMovesTheClothChainsOfItsGarments()
        {
            var body = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
            Assert.That(body.GetComponent<ClothChainSolver>(), Is.Not.Null);
        }
    }
}
