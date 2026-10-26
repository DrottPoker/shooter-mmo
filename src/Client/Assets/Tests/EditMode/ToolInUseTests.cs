using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Tests.EditMode
{
    // A tool, such as a pickaxe, is held only while its character uses it: it takes both hands,
    // the body plays the tool's clip, and the other hand is pinned on the tool's haft, as
    // docs/EQUIPMENT_VISUALS_DESIGN.md describes under Tools In Use.
    public sealed class ToolInUseTests
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
        public void EveryToolItemShowsItsToolOnlyFromTheToolSlot()
        {
            Assert.That(AttachmentOf("tool.starter_pickaxe"), Is.EqualTo("MiningPickaxe"));
            Assert.That(AttachmentOf("tool.woodcutting_axe"), Is.EqualTo("WoodcuttingAxe"));
            foreach (var catalog in Catalogs())
            {
                foreach (var definition in bundled.Definitions)
                {
                    Assert.That(bundled.Presentation.TryGet(definition.Id, out var entry), Is.True, definition.Id);
                    var attachment = catalog.AttachmentNamed(entry.attachment);
                    if (attachment == null || !attachment.IsTool)
                    {
                        continue;
                    }

                    // A tool is held while it is used, and only an item of the Tool slot is used.
                    Assert.That(definition.EquipmentSlots, Is.All.EqualTo(ItemEquipmentSlotIds.Tool), definition.Id + " on " + catalog.name);
                }
            }
        }

        [Test]
        public void EveryToolHasALoopingStateOfItsClipAndItsOtherHandOnItsHaft()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            var states = controller.layers[0].stateMachine.states.Select(child => child.state).ToArray();
            var tools = Catalogs().SelectMany(catalog => catalog.Attachments).Where(attachment => attachment.IsTool).ToArray();
            Assert.That(tools.Select(tool => tool.Name).Distinct(), Is.EquivalentTo(new[] { "MiningPickaxe", "WoodcuttingAxe" }));
            foreach (var tool in tools)
            {
                var state = states.SingleOrDefault(candidate => candidate.name == tool.UseClip);
                Assert.That(state, Is.Not.Null, tool.Name + " plays " + tool.UseClip + ", which the base layer lacks");
                var clip = state.motion as AnimationClip;
                Assert.That(clip, Is.Not.Null, tool.UseClip);
                Assert.That(clip.name, Is.EqualTo(tool.UseClip));
                Assert.That(clip.isLooping, Is.True, tool.UseClip);
                // The state is entered and left by the body, never by a transition of its own.
                Assert.That(state.transitions, Is.Empty, tool.UseClip);

                // Both tools are held with both hands, the other hand on the leather near the butt.
                Assert.That(tool.HoldsWithBothHands, Is.True, tool.Name);
                var nearest = ModelVertices(tool.Model).Min(vertex => (vertex - tool.OffHandGrip).magnitude);
                Assert.That(nearest, Is.LessThan(0.03f), tool.Name + " has no haft where the other hand holds it");
            }
        }

        [Test]
        public void AToolInUseTakesBothHandsAndIsPutAwayWhenNotUsed()
        {
            foreach (var catalog in Catalogs())
            {
                var equipment = new[]
                {
                    new EquippedItem("main_hand", "weapon.iron_sword"),
                    new EquippedItem("off_hand", "armor.heater_shield"),
                    new EquippedItem("tool", "tool.starter_pickaxe"),
                };
                var armed = EquipmentGarments.Dress(BodyLook.Default, catalog, equipment, bundled.Presentation, false);
                Assert.That(armed.Attachments, Is.EquivalentTo(new[] { "HeaterShield", "IronSword" }), catalog.name);

                var mining = EquipmentGarments.Dress(BodyLook.Default, catalog, equipment, bundled.Presentation, false, "tool.starter_pickaxe");
                Assert.That(mining.Attachments, Is.EqualTo(new[] { "MiningPickaxe" }), catalog.name);

                // Another player's tool comes by its definition alone, without its slot.
                var chopping = EquipmentGarments.Dress(BodyLook.Default, catalog, null, bundled.Presentation, false, "tool.woodcutting_axe");
                Assert.That(chopping.Attachments, Is.EqualTo(new[] { "WoodcuttingAxe" }), catalog.name);

                // An item that is no tool is never held as one.
                var sword = EquipmentGarments.Dress(BodyLook.Default, catalog, equipment, bundled.Presentation, false, "weapon.iron_sword");
                Assert.That(sword.Attachments, Is.EquivalentTo(new[] { "HeaterShield", "IronSword" }), catalog.name);
            }
        }

        [Test]
        public void TheLocalPlayerUsesTheItemOfItsUsedSlot()
        {
            var equipment = new PlayerEquipment();
            var changes = 0;
            equipment.Changed += () => changes++;
            equipment.Set(new[] { new EquippedItem("tool", "tool.woodcutting_axe") }, bundled.Presentation);
            Assert.That(equipment.ToolInUse, Is.Empty);

            equipment.Use(ItemEquipmentSlotIds.Tool);
            equipment.Use(ItemEquipmentSlotIds.Tool);
            Assert.That(equipment.ToolInUse, Is.EqualTo("tool.woodcutting_axe"));
            Assert.That(changes, Is.EqualTo(2));

            equipment.Use(null);
            Assert.That(equipment.ToolInUse, Is.Empty);
            Assert.That(changes, Is.EqualTo(3));
        }

        // The clips are keyed on the male body and retargeted, so the other hand lands near the
        // haft; pinned, its socket lies on the tool's grip point, turned so both thumbs point along
        // the haft, on both bodies through the whole swing.
        [Test]
        public void ABodyUsingAToolHoldsItWithBothHandsThroughTheSwing()
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
                        body.transform.SetPositionAndRotation(new Vector3(-2f, 0.3f, 4f), Quaternion.Euler(0f, -70f, 0f));
                        var appearance = body.GetComponent<PlayerBodyAppearance>();
                        appearance.CombineParts(meshes);
                        var animator = body.GetComponent<Animator>();
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                        var mainSocket = Bone(body, HumanLocomotion.MainHandSocketBone);
                        var offSocket = Bone(body, HumanLocomotion.OffHandSocketBone);
                        foreach (var (toolName, clip) in new[] { ("MiningPickaxe", HumanLocomotion.MineClip), ("WoodcuttingAxe", HumanLocomotion.ChopClip) })
                        {
                            appearance.ApplyLook(BodyLook.Default.WithAttachment(toolName, true), materials);
                            Assert.That(appearance.Tool, Is.Not.Null, toolName);
                            Assert.That(appearance.Tool.UseClip, Is.EqualTo(clip));
                            Assert.That(animator.GetLayerWeight(animator.GetLayerIndex(HumanLocomotion.OffHandGripLayer)), Is.EqualTo(1f),
                                "the other hand closes around the haft");
                            var where = path + " " + clip;
                            var worstRetargeted = 0f;
                            for (var sample = 0; sample < 9; sample++)
                            {
                                animator.Play(clip, 0, sample / 9f);
                                animator.Update(0f);
                                OffHandToolGrip.Target(mainSocket, appearance.Tool.OffHandGrip, out var position, out var rotation);
                                worstRetargeted = Mathf.Max(worstRetargeted, (offSocket.position - position).magnitude);

                                // Where the swing carries the haft beyond the arm's reach, the
                                // collar bone brings the shoulder forward.
                                appearance.PinOffHand(1f);
                                Assert.That((offSocket.position - position).magnitude, Is.LessThan(0.001f), where + " at " + sample);
                                Assert.That(Quaternion.Angle(offSocket.rotation, rotation), Is.LessThan(1f), where + " at " + sample);
                            }

                            // The clip already holds the haft on the body it was keyed on; on another
                            // body retargeting moves the hand by centimeters, which the pin takes back.
                            Assert.That(worstRetargeted, Is.LessThan(0.12f), where);
                            TestContext.WriteLine(where + ": the retargeted other hand lies at most " + worstRetargeted.ToString("F3") + " m from the haft");
                        }
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private BodyAppearanceCatalog[] Catalogs()
        {
            var catalogs = new[] { "Human_Male", "Human_Female" }
                .Select(model => AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>("Assets/Art/Appearance/" + model + "_Appearance.asset"))
                .ToArray();
            Assert.That(catalogs, Has.None.Null);
            return catalogs;
        }

        private string AttachmentOf(string definitionId)
        {
            Assert.That(bundled.Presentation.TryGet(definitionId, out var entry), Is.True, definitionId);
            return entry.attachment;
        }

        private static Transform Bone(GameObject body, string name)
        {
            var bone = body.GetComponentsInChildren<Transform>().SingleOrDefault(candidate => candidate.name == name);
            Assert.That(bone, Is.Not.Null, name);
            return bone;
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
