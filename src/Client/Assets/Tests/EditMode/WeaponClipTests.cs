using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Tests.EditMode
{
    // A weapon with clips, such as the Oak Staff, is held in its pose on the main hand's arm, plays
    // its attack clip over the upper body when its attack releases, and launches its projectile
    // from its launch point, as docs/EQUIPMENT_VISUALS_DESIGN.md describes under Weapon Poses And
    // Attack Clips.
    public sealed class WeaponClipTests
    {
        private const string ControllerPath = "Assets/Art/Animation/HumanLocomotion.controller";

        private static readonly string[] BodyPrefabPaths =
        {
            "Assets/Prefabs/Player/HumanMaleBody.prefab",
            "Assets/Prefabs/Player/HumanFemaleBody.prefab",
        };

        private ClientConfig config;
        private AsyncOperationHandle<ClientConfig> configHandle;
        private ClientItemCatalog bundled;

        [SetUp]
        public void SetUp()
        {
            ItemPresentationCatalogLoader.ResetCache();
            config = ClientConfig.Load(out configHandle);
            Assert.That(ClientItemCatalog.TryLoad(config.ItemGameplayCatalog, out bundled, out var error), Is.True, error);
        }

        [TearDown]
        public void TearDown()
        {
            ItemPresentationCatalogLoader.ResetCache();
            ClientAssets.Release(configHandle);
            configHandle = default;
        }

        [Test]
        public void TheOakStaffNamesItsPoseItsCastAndItsCrystal()
        {
            Assert.That(bundled.Presentation.TryGet("weapon.oak_staff", out var entry), Is.True);
            Assert.That(entry.attachment, Is.EqualTo("OakStaff"));
            foreach (var catalog in Catalogs())
            {
                var staff = catalog.AttachmentNamed("OakStaff");
                Assert.That(staff, Is.Not.Null, catalog.name);
                Assert.That(staff.IsTool, Is.False);
                Assert.That(staff.Bone, Is.EqualTo(HumanLocomotion.MainHandSocketBone));
                Assert.That(staff.HoldClip, Is.EqualTo(HumanLocomotion.HoldStaffClip));
                Assert.That(staff.AttackClip, Is.EqualTo(HumanLocomotion.CastClip));
                Assert.That(staff.HasLaunchPoint, Is.True);
                // The bolt leaves from the crystal, which lies on the model 70 cm along the shaft.
                var nearest = ModelVertices(staff.Model).Min(vertex => (vertex - staff.LaunchPoint).magnitude);
                Assert.That(nearest, Is.LessThan(0.05f), "the launch point lies off the staff");
                Assert.That(staff.LaunchPoint.magnitude, Is.EqualTo(0.7f).Within(0.01f));
            }
        }

        [Test]
        public void EveryWeaponClipHasAStateOnItsLayerWithItsMask()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            var layers = controller.layers;
            var hold = layers.SingleOrDefault(layer => layer.name == HumanLocomotion.MainHandHoldLayer);
            var action = layers.SingleOrDefault(layer => layer.name == HumanLocomotion.ActionLayer);
            Assert.That(hold, Is.Not.Null);
            Assert.That(action, Is.Not.Null);
            // The attack plays over the hold pose, which plays over the locomotion clips.
            Assert.That(System.Array.IndexOf(layers, action), Is.GreaterThan(System.Array.IndexOf(layers, hold)));
            AssertMask(hold.avatarMask, AvatarMaskBodyPart.RightArm);
            AssertMask(action.avatarMask, AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head, AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.RightArm);
            foreach (var layer in new[] { hold, action })
            {
                Assert.That(layer.defaultWeight, Is.Zero, layer.name);
                Assert.That(layer.blendingMode, Is.EqualTo(AnimatorLayerBlendingMode.Override), layer.name);
                Assert.That(layer.stateMachine.defaultState.name, Is.EqualTo(HumanLocomotion.EmptyState), layer.name);
                Assert.That(layer.stateMachine.defaultState.motion, Is.Null, layer.name);
            }

            var weapons = Catalogs().SelectMany(catalog => catalog.Attachments)
                .Where(attachment => attachment.HoldClip.Length > 0 || attachment.AttackClip.Length > 0)
                .ToArray();
            Assert.That(weapons.Select(weapon => weapon.Name).Distinct(), Is.EquivalentTo(new[] { "OakStaff" }));
            foreach (var weapon in weapons)
            {
                // The hold pose loops; the attack plays once and the body fades it out.
                AssertClipState(hold, weapon.HoldClip, true);
                AssertClipState(action, weapon.AttackClip, false);
            }
        }

        [Test]
        public void AnAttackClipFadesInPlaysOnceAndFadesOut()
        {
            const float length = 0.9f;
            Assert.That(HumanLocomotion.ActionWeight(-0.01f, length), Is.Zero);
            Assert.That(HumanLocomotion.ActionWeight(0f, length), Is.Zero);
            Assert.That(HumanLocomotion.ActionWeight(HumanLocomotion.ActionFadeInSeconds * 0.5f, length), Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(HumanLocomotion.ActionWeight(0.4f, length), Is.EqualTo(1f));
            Assert.That(
                HumanLocomotion.ActionWeight(length - (HumanLocomotion.ActionFadeOutSeconds * 0.5f), length),
                Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(HumanLocomotion.ActionWeight(length, length), Is.Zero);
        }

        // On both bodies, retargeted from the male rig, the staff stands upright beside the body in
        // the hold pose with its foot above the ground, and the thrust of Cast tips its crystal
        // forward toward the target.
        [Test]
        public void ABodyHoldsTheStaffUprightAndThrustsItForward()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                using (var materials = new PlayerBodyMaterials())
                {
                    foreach (var path in BodyPrefabPaths)
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                        body.transform.SetPositionAndRotation(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f));
                        var appearance = body.GetComponent<PlayerBodyAppearance>();
                        appearance.CombineParts(meshes);
                        appearance.ApplyLook(BodyLook.Default.WithAttachment("OakStaff", true), materials);
                        Assert.That(appearance.Weapon, Is.Not.Null, path);
                        Assert.That(appearance.Weapon.Name, Is.EqualTo("OakStaff"));
                        Assert.That(body.GetComponent<CharacterLocomotionAnimator>().HoldClip, Is.EqualTo(HumanLocomotion.HoldStaffClip));
                        var animator = body.GetComponent<Animator>();
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                        var holdLayer = animator.GetLayerIndex(HumanLocomotion.MainHandHoldLayer);
                        var actionLayer = animator.GetLayerIndex(HumanLocomotion.ActionLayer);
                        var socket = body.GetComponentsInChildren<Transform>().Single(bone => bone.name == HumanLocomotion.MainHandSocketBone);

                        animator.Play(HumanLocomotion.IdleState, 0, 0f);
                        animator.Play(HumanLocomotion.HoldStaffClip, holdLayer, 0f);
                        animator.SetLayerWeight(holdLayer, 1f);
                        animator.Update(0f);
                        Assert.That(appearance.TryGetLaunchPoint(out var crystal), Is.True, path);
                        var shaft = crystal - socket.position;
                        Assert.That(Vector3.Angle(shaft, Vector3.up), Is.LessThan(15f), path + " holds the staff upright");
                        var foot = socket.position - (shaft.normalized * 0.92f);
                        Assert.That(foot.y, Is.InRange(0.0f, 0.3f), path + " holds the staff's foot just above the ground");
                        var side = body.transform.InverseTransformPoint(socket.position).x;
                        Assert.That(side, Is.GreaterThan(0.15f), path + " holds the staff at its right side");

                        // The thrust, Cast's sixth frame of 27, over the hold pose.
                        animator.Play(HumanLocomotion.CastClip, actionLayer, 6f / 27f);
                        animator.SetLayerWeight(actionLayer, 1f);
                        animator.Update(0f);
                        Assert.That(appearance.TryGetLaunchPoint(out var thrust), Is.True, path);
                        var tilt = Vector3.Angle(thrust - socket.position, Vector3.up);
                        Assert.That(tilt, Is.InRange(15f, 60f), path + " tips the staff forward");
                        var ahead = body.transform.InverseTransformPoint(thrust).z;
                        Assert.That(ahead, Is.GreaterThan(0.5f), path + " points the crystal at the target");
                        TestContext.WriteLine(path + ": the staff leans " + Vector3.Angle(shaft, Vector3.up).ToString("F1")
                            + " degrees at rest and " + tilt.ToString("F1") + " in the thrust, its crystal " + ahead.ToString("F2") + " m ahead");
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void AssertMask(AvatarMask mask, params AvatarMaskBodyPart[] active)
        {
            Assert.That(mask, Is.Not.Null);
            for (var part = AvatarMaskBodyPart.Root; part < AvatarMaskBodyPart.LastBodyPart; part++)
            {
                Assert.That(mask.GetHumanoidBodyPartActive(part), Is.EqualTo(active.Contains(part)), mask.name + " " + part);
            }
        }

        private static void AssertClipState(AnimatorControllerLayer layer, string clipName, bool looping)
        {
            var state = layer.stateMachine.states.Select(child => child.state).SingleOrDefault(candidate => candidate.name == clipName);
            Assert.That(state, Is.Not.Null, layer.name + " lacks the state " + clipName);
            var clip = state.motion as AnimationClip;
            Assert.That(clip, Is.Not.Null, clipName);
            Assert.That(clip.name, Is.EqualTo(clipName));
            Assert.That(clip.isLooping, Is.EqualTo(looping), clipName);
            // The body enters and leaves the state, never a transition of its own.
            Assert.That(state.transitions, Is.Empty, clipName);
        }

        private static BodyAppearanceCatalog[] Catalogs()
        {
            var catalogs = new[] { "Human_Male", "Human_Female" }
                .Select(model => AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>("Assets/Art/Appearance/" + model + "_Appearance.asset"))
                .ToArray();
            Assert.That(catalogs, Has.None.Null);
            return catalogs;
        }

        // Every vertex of a model in its own space, which is its socket's.
        private static IEnumerable<Vector3> ModelVertices(GameObject model)
        {
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                var toModel = model.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                foreach (var vertex in filter.sharedMesh.vertices)
                {
                    yield return toModel.MultiplyPoint3x4(vertex);
                }
            }
        }
    }
}
