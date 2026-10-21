using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class PlayerBodyMeshesTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";

        [Test]
        public void ACombinedBodySkinsEveryVertexWhereItsPartsDo()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    var body = InstantiateBody(scene);
                    var parts = body.GetComponentsInChildren<SkinnedMeshRenderer>().ToArray();
                    Assert.That(parts.Length, Is.GreaterThan(1));
                    Pose(body);

                    // The parts as they skin in this pose, in the body's space.
                    var expected = new List<Vector3>();
                    var baked = new Mesh();
                    foreach (var part in parts)
                    {
                        part.BakeMesh(baked, true);
                        var toBody = body.transform.worldToLocalMatrix * part.transform.localToWorldMatrix;
                        expected.AddRange(baked.vertices.Select(vertex => toBody.MultiplyPoint3x4(vertex)));
                    }

                    var appearance = body.GetComponent<PlayerBodyAppearance>();
                    appearance.CombineParts(meshes);
                    Pose(body);
                    var combined = appearance.CombinedRenderer;
                    Assert.That(combined, Is.Not.Null);
                    Assert.That(parts.All(part => !part.enabled), Is.True);
                    combined.BakeMesh(baked, true);
                    var toBodyCombined = body.transform.worldToLocalMatrix * combined.transform.localToWorldMatrix;
                    var actual = baked.vertices.Select(vertex => toBodyCombined.MultiplyPoint3x4(vertex)).ToArray();
                    Object.DestroyImmediate(baked);

                    Assert.That(actual.Length, Is.EqualTo(expected.Count));
                    var largest = 0f;
                    for (var index = 0; index < actual.Length; index++)
                    {
                        largest = Mathf.Max(largest, (actual[index] - expected[index]).magnitude);
                    }

                    Assert.That(largest, Is.LessThan(0.0005f), "largest vertex difference in meters");
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void ACombinedBodyIsBoundedAroundEveryPart()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    var body = InstantiateBody(scene);
                    var parts = body.GetComponentsInChildren<SkinnedMeshRenderer>()
                        .ToDictionary(part => part.name, part => part.bounds);
                    var appearance = body.GetComponent<PlayerBodyAppearance>();
                    appearance.CombineParts(meshes);
                    var combined = appearance.CombinedRenderer.bounds;
                    combined.Expand(0.001f);
                    foreach (var part in parts)
                    {
                        Assert.That(combined.Contains(part.Value.min), Is.True, part.Key);
                        Assert.That(combined.Contains(part.Value.max), Is.True, part.Key);
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void BodiesWithTheSameVisiblePartsShareOneMeshWithOneSubmeshPerMaterial()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    var first = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                    var second = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                    var materials = first.GetComponentsInChildren<SkinnedMeshRenderer>()
                        .SelectMany(part => part.sharedMaterials)
                        .Distinct()
                        .ToArray();
                    first.CombineParts(meshes);
                    second.CombineParts(meshes);

                    Assert.That(meshes.Count, Is.EqualTo(1));
                    Assert.That(first.CombinedRenderer.sharedMesh, Is.SameAs(second.CombinedRenderer.sharedMesh));
                    Assert.That(first.CombinedRenderer.sharedMesh.subMeshCount, Is.EqualTo(materials.Length));
                    Assert.That(first.CombinedRenderer.sharedMaterials, Is.EquivalentTo(materials));
                    // Each body skins the shared mesh with its own bones.
                    Assert.That(
                        first.CombinedRenderer.bones.All(bone => bone.IsChildOf(first.transform)),
                        Is.True);
                    Assert.That(
                        second.CombinedRenderer.bones.All(bone => bone.IsChildOf(second.transform)),
                        Is.True);

                    // Combining again changes nothing.
                    first.CombineParts(meshes);
                    Assert.That(
                        first.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                            .Count(part => part.name == PlayerBodyAppearance.CombinedRendererName),
                        Is.EqualTo(1));
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void ATintedCombinedBodyTintsOnlyItsSkin()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                using (var tints = new PlayerBodyTintMaterials())
                {
                    var appearance = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                    appearance.CombineParts(meshes);
                    var own = appearance.CombinedRenderer.sharedMaterials;

                    appearance.ApplyTint(true, PlayerBodyColors.Player, tints);
                    var tinted = appearance.CombinedRenderer.sharedMaterials;
                    for (var index = 0; index < own.Length; index++)
                    {
                        var skin = appearance.SkinMaterials.Contains(own[index]);
                        Assert.That(tinted[index] != own[index], Is.EqualTo(skin), own[index].name);
                    }

                    appearance.ApplyTint(false, PlayerBodyColors.Player, tints);
                    Assert.That(appearance.CombinedRenderer.sharedMaterials, Is.EqualTo(own));
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static GameObject InstantiateBody(UnityEngine.SceneManagement.Scene scene)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
            var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            body.transform.SetPositionAndRotation(new Vector3(3f, 0.5f, -2f), Quaternion.Euler(0f, 40f, 0f));
            return body;
        }

        // A jog pose, so every bone is away from its rest pose.
        private static void Pose(GameObject body)
        {
            var animator = body.GetComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            animator.SetBool(HumanLocomotion.MovingParameter, true);
            animator.SetFloat(HumanLocomotion.SpeedParameter, HumanLocomotion.JogSpeed);
            for (var frame = 0; frame < 12; frame++)
            {
                animator.Update(1f / 30f);
            }
        }
    }
}
