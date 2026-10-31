using System;
using System.Linq;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The Wardrobe preview dresses the bodies, plays their clips on a moving ground, and runs the
    // cloth solver, as docs/ART_PIPELINE.md describes under The Wardrobe.
    public sealed class WardrobePreviewTests
    {
        private const string Jerkin = "LeatherJerkin";
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";

        // Tests that draw, which tools/Run-UnityTests.ps1 leaves out; see tests/README.md.
        private const string GraphicsCategory = "Graphics";

        // A model without an Animator.
        private const string IronVeinPath = "Assets/Art/Models/WorldActors/Resource_IronVein.fbx";

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

        // A body still draws after its outfit changes the size of its combined mesh. Unity 6000.6 stopped
        // drawing an enabled skinned renderer whose mesh changed to one of another size, with the error
        // "does not match the expected mesh data size and vertex stride", which fails the test. It needs
        // a graphics device, so the headless run with -nographics leaves its category out.
        [Test]
        [Category(GraphicsCategory)]
        public void ABodyDrawsAfterEveryChangeOfItsOutfit()
        {
            foreach (var body in WardrobePreview.Bodies())
            {
                using (var preview = new WardrobePreview(body))
                {
                    var cameraObject = new GameObject("TestCamera") { hideFlags = HideFlags.HideAndDontSave };
                    var target = new RenderTexture(64, 64, 24);
                    try
                    {
                        var camera = cameraObject.AddComponent<Camera>();
                        camera.enabled = false;
                        camera.scene = preview.Scene;
                        camera.targetTexture = target;
                        camera.transform.position = preview.Focus + Vector3.forward * 3f;
                        camera.transform.LookAt(preview.Focus);
                        camera.Render();
                        foreach (var outfit in new[] { new[] { "WoolRobe" }, new[] { "WoolRobe", "WoolCloak" }, new string[0] })
                        {
                            preview.Wear(outfit);
                            preview.Step(1f / 60f, Vector3.back * 3f);
                            camera.Render();
                        }
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(cameraObject);
                        UnityEngine.Object.DestroyImmediate(target);
                    }
                }
            }
        }

        [Test]
        public void TheWardrobeDressesEveryBodyInBootsAndGlovesOverItsTrousersAndSleeves()
        {
            var outfit = new[] { "LeatherGloves", "LinenLongShirt", "LeatherBoots", "WoolTrousers" };
            foreach (var body in WardrobePreview.Bodies())
            {
                using (var preview = new WardrobePreview(body))
                {
                    var renderer = preview.Body.GetComponent<PlayerBodyAppearance>().CombinedRenderer;
                    var bare = renderer.sharedMesh.triangles.Length;
                    preview.Wear(outfit);
                    // From the inside out: the trousers, the boots over their legs, the shirt, and the
                    // gloves over its sleeves.
                    Assert.That(preview.Worn.Select(garment => garment.Name),
                        Is.EqualTo(new[] { "WoolTrousers", "LeatherBoots", "LinenLongShirt", "LeatherGloves" }), body.Name);
                    Assert.That(preview.Worn.Select(garment => garment.Slot), Is.EqualTo(new[] { "legs", "feet", "body", "hands" }), body.Name);
                    var materials = renderer.sharedMaterials.Select(material => material.name).ToArray();
                    foreach (var name in outfit)
                    {
                        Assert.That(materials, Does.Contain("M_Garment_" + name), body.Name);
                    }

                    // The boots and the gloves take the place of the feet and the hands they cover, so
                    // the dressed body draws fewer of its own triangles than the bare one.
                    var skin = renderer.sharedMaterials.Select((material, index) => (material, index))
                        .Where(entry => !entry.material.name.StartsWith("M_Garment_", StringComparison.Ordinal))
                        .Sum(entry => renderer.sharedMesh.GetTriangles(entry.index).Length);
                    Assert.That(skin, Is.LessThan(bare), body.Name);
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
                    preview.Step(0.1f, new Vector3(0f, 0.4f, 3f));
                }

                Assert.That(preview.Body.transform.position.z, Is.EqualTo(HumanLocomotion.JogSpeed).Within(0.001f));
                Assert.That(Quaternion.Angle(hips.localRotation, rest), Is.GreaterThan(0.5f), "the body jogs");
                var ground = preview.Scene.GetRootGameObjects().Single(root => root.name == "WardrobeGround").transform;
                Assert.That(Mathf.Abs(ground.position.z - preview.Body.transform.position.z), Is.LessThanOrEqualTo(WardrobePreview.TileSize));

                preview.Play(WardrobePreview.Motion.Idle);
                var stopped = preview.Body.transform.position;
                preview.Step(0.5f, Vector3.forward * 3f);
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
                    preview.Step(0.1f, Vector3.forward * 3f);
                }

                var animator = preview.Body.GetComponent<Animator>();
                Assert.That(animator.GetCurrentAnimatorStateInfo(0).IsName(HumanLocomotion.DeathState), Is.True);
                Assert.That(preview.Speed, Is.Zero);
            }
        }

        // A staff stands in its hold pose as in the game, Cast plays over it and starts again when it
        // ends, and a tool's clip puts the pose away.
        [Test]
        public void TheWardrobeHoldsTheStaffInItsPoseAndCastsWithIt()
        {
            using (var preview = new WardrobePreview(WardrobePreview.Bodies()[0]))
            {
                preview.Wear(Array.Empty<string>(), null, new[] { "OakStaff" });
                var animator = preview.Body.GetComponent<Animator>();
                var hold = animator.GetLayerIndex(HumanLocomotion.MainHandHoldLayer);
                var action = animator.GetLayerIndex(HumanLocomotion.ActionLayer);
                Assert.That(animator.GetLayerWeight(hold), Is.EqualTo(1f));
                Assert.That(animator.GetLayerWeight(action), Is.Zero);

                preview.Play(WardrobePreview.Motion.Cast);
                Assert.That(animator.GetLayerWeight(action), Is.EqualTo(1f));
                for (var step = 0; step < 12; step++)
                {
                    preview.Step(0.1f, Vector3.forward * 3f);
                }

                var state = animator.GetCurrentAnimatorStateInfo(action);
                Assert.That(state.IsName(HumanLocomotion.ActionLayer + "." + HumanLocomotion.CastClip), Is.True);
                Assert.That(state.normalizedTime, Is.LessThan(1f), "Cast started again");

                preview.Play(WardrobePreview.Motion.Mine);
                Assert.That(animator.GetLayerWeight(hold), Is.Zero);
                Assert.That(animator.GetLayerWeight(action), Is.Zero);
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
        public void AModelThatCannotBeShownLeavesNoSceneBehind()
        {
            var vein = AssetDatabase.LoadAssetAtPath<GameObject>(IronVeinPath);
            var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(ArtAppearanceCatalogs.PathFor(WardrobePreview.Bodies()[0]));
            var scenes = EditorSceneManager.previewSceneCount;

            var error = Assert.Throws<InvalidOperationException>(() => new WardrobePreview(IronVeinPath, vein, catalog));
            Assert.That(error.Message, Does.Contain("has no Animator"));
            Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
        }

        [Test]
        public void TheBodyPrefabMovesTheClothChainsOfItsGarments()
        {
            var body = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
            Assert.That(body.GetComponent<ClothChainSolver>(), Is.Not.Null);
        }
    }
}
