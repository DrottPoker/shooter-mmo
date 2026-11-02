using System;
using System.Linq;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Tests.EditMode
{
    // The copy of the local character that the Equipment window shows, on its stage far below the
    // world. See docs/UNITY_CLIENT_ARCHITECTURE.md under Character Preview.
    public sealed class CharacterPreviewTests
    {
        private const string BodyModelsPath = "Assets/Content/Player/PlayerBodyModels.asset";
        private const string LocalPlayerPrefabPath = "Assets/Prefabs/Player/LocalPlayer.prefab";

        // Tests that draw, which tools/Run-UnityTests.ps1 leaves out; see tests/README.md.
        private const string GraphicsCategory = "Graphics";

        // No World reaches deeper than this below its origin.
        private const float WorldDepth = 1000f;

        [Test]
        public void NoPlayerCameraSeesTheStageAndTheStageCameraSeesNoWorld()
        {
            var depth = -CharacterPreview.StagePosition.y;
            var player = AssetDatabase.LoadAssetAtPath<GameObject>(LocalPlayerPrefabPath);
            var cameras = player.GetComponentsInChildren<Camera>(true);
            Assert.That(cameras, Is.Not.Empty);
            foreach (var camera in cameras)
            {
                Assert.That(camera.farClipPlane, Is.LessThan(depth - WorldDepth), camera.name);
            }

            Assert.That(
                CharacterPreview.CameraHeight + CharacterPreview.CameraFarPlane,
                Is.LessThan(depth - WorldDepth));
        }

        // The copy stands on the stage in the look it is given and fits the camera's view; another
        // look keeps the copy, another model puts in another one, and turning turns the copy
        // around its height.
        [Test]
        public void TheCopyShowsTheModelInTheLookAndTurnsAroundItsHeight()
        {
            var models = AssetDatabase.LoadAssetAtPath<PlayerBodyModels>(BodyModelsPath);
            var male = models.NamedOrDefault("Human_Male");
            var female = models.NamedOrDefault("Human_Female");
            var garment = male.Body.Catalog.Garments.First(candidate => candidate != null).Name;
            OnStage((preview, meshes, materials) =>
            {
                Assert.That(preview.transform.position, Is.EqualTo(CharacterPreview.StagePosition));
                Assert.That(preview.Body == null, Is.True);

                preview.Show(male, BodyLook.Default.WithGarment(garment, true), meshes, materials);
                var body = preview.Body;
                Assert.That(body.name, Is.EqualTo(CharacterPreview.BodyName));
                Assert.That(body.Catalog, Is.SameAs(male.Body.Catalog));
                Assert.That(body.transform.position, Is.EqualTo(CharacterPreview.StagePosition));
                Assert.That(body.CombinedRenderer, Is.Not.Null);
                Assert.That(body.Worn.Select(worn => worn.Name), Is.EqualTo(new[] { garment }));
                Assert.That(body.GetComponent<Animator>().cullingMode, Is.EqualTo(AnimatorCullingMode.CullCompletely));
                var planes = GeometryUtility.CalculateFrustumPlanes(preview.Camera);
                Assert.That(GeometryUtility.TestPlanesAABB(planes, body.CombinedRenderer.bounds), Is.True);

                preview.Show(male, BodyLook.Default, meshes, materials);
                Assert.That(preview.Body, Is.SameAs(body));
                Assert.That(body.Worn, Is.Empty);

                preview.Show(female, BodyLook.Default, meshes, materials);
                Assert.That(body == null, Is.True, "the male copy is gone");
                Assert.That(preview.Body.Catalog, Is.SameAs(female.Body.Catalog));
                Assert.That(preview.GetComponentsInChildren<PlayerBodyAppearance>(true), Is.EqualTo(new[] { preview.Body }));

                Assert.That(Vector3.Dot(preview.Body.transform.forward, preview.Camera.transform.forward), Is.EqualTo(-1f).Within(1e-4f),
                    "the copy faces the camera");
                preview.Yaw = 90f;
                Assert.That((preview.Body.transform.forward - Vector3.right).magnitude, Is.LessThan(1e-4f));
                preview.Yaw = -30f;
                Assert.That(preview.Yaw, Is.EqualTo(330f).Within(1e-4f));
            });
        }

        // With a graphics device the camera draws into a texture of the preview's size, and only
        // while the preview is visible.
        [Test]
        [Category(GraphicsCategory)]
        public void TheCameraDrawsIntoTheTextureOnlyWhileVisible()
        {
            OnStage((preview, meshes, materials) =>
            {
                Assert.That(preview.Texture.width, Is.EqualTo(CharacterPreview.TextureWidth));
                Assert.That(preview.Texture.height, Is.EqualTo(CharacterPreview.TextureHeight));
                Assert.That(preview.Camera.targetTexture, Is.SameAs(preview.Texture));
                Assert.That(preview.Visible, Is.False);

                preview.Visible = true;
                Assert.That(preview.Camera.enabled, Is.True);
                preview.Visible = false;
                Assert.That(preview.Camera.enabled, Is.False);
            });
        }

        // Runs a test on a preview below a World scene's context in a scene of its own.
        private static void OnStage(Action<CharacterPreview, PlayerBodyMeshes, PlayerBodyMaterials> test)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("WorldSceneContext");
            SceneManager.MoveGameObjectToScene(root, scene);
            CharacterPreview preview = null;
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                using (var materials = new PlayerBodyMaterials())
                {
                    preview = CharacterPreview.Create(root.transform);
                    Assert.That(preview.gameObject.scene, Is.EqualTo(scene));
                    test(preview, meshes, materials);
                }
            }
            finally
            {
                var texture = preview != null ? preview.Texture : null;
                EditorSceneManager.ClosePreviewScene(scene);
                if (texture != null)
                {
                    texture.Release();
                    Object.DestroyImmediate(texture);
                }
            }
        }
    }
}
