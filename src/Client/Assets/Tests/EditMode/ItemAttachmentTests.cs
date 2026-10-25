using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Tests.EditMode
{
    // Items show the attachments the item presentation names, such as a sword, held on the sockets
    // of their equipment slots with the hand closed around the grip, as docs/EQUIPMENT_VISUALS_DESIGN.md
    // describes under Attachments.
    public sealed class ItemAttachmentTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";

        private static readonly string[] BodyCatalogPaths =
        {
            "Assets/Art/Appearance/Human_Male_Appearance.asset",
            "Assets/Art/Appearance/Human_Female_Appearance.asset",
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
        public void EveryAttachmentOfAnItemIsHeldOnTheSocketOfEachOfItsSlots()
        {
            var catalogs = BodyCatalogPaths.Select(AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>).ToArray();
            Assert.That(catalogs, Has.None.Null);
            Assert.That(AttachmentOf("weapon.iron_sword"), Is.EqualTo("IronSword"));
            Assert.That(AttachmentOf("armor.heater_shield"), Is.EqualTo("HeaterShield"));
            foreach (var definition in bundled.Definitions)
            {
                Assert.That(bundled.Presentation.TryGet(definition.Id, out var entry), Is.True, definition.Id);
                if (string.IsNullOrEmpty(entry.attachment))
                {
                    continue;
                }

                foreach (var catalog in catalogs)
                {
                    var where = definition.Id + " on " + catalog.name;
                    var attachment = catalog.AttachmentNamed(entry.attachment);
                    Assert.That(attachment, Is.Not.Null, where + " names the attachment " + entry.attachment + ", which the body's catalog lacks");
                    Assert.That(attachment.Model, Is.Not.Null, where);
                    // The item shows it in every slot it can be equipped in.
                    Assert.That(definition.EquipmentSlots, Is.All.EqualTo(attachment.Socket), where);
                }
            }
        }

        [Test]
        public void AnAttachmentModelIsHeldAtItsGrip()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(BodyCatalogPaths[0]);
            Assert.That(catalog.Attachments, Is.Not.Empty);
            foreach (var attachment in catalog.Attachments)
            {
                var grip = attachment.Model.transform.Find("Grip");
                Assert.That(grip, Is.Not.Null, attachment.Name);
                Assert.That(attachment.Model.transform.childCount, Is.EqualTo(1), attachment.Name);
                // The model's own frame is the socket's, as the review sheet holds it in Blender.
                Assert.That(grip.localPosition.magnitude, Is.LessThan(1e-5f), attachment.Name);
                Assert.That(Quaternion.Angle(grip.localRotation, Quaternion.identity), Is.LessThan(0.01f), attachment.Name);
                Assert.That(attachment.Model.GetComponentsInChildren<MeshFilter>().All(filter => filter.sharedMesh.isReadable), Is.True,
                    attachment.Name + " must import readable, since the body combines it");
            }
        }

        [Test]
        public void EquipmentPutsEachAttachmentOnTheSocketOfItsSlot()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(BodyCatalogPaths[0]);
            var own = BodyLook.Default.WithGarment("DarkBrownHair", true).WithAttachment("HeaterShield", true);

            var armed = EquipmentGarments.Dress(own, catalog, new[]
            {
                new EquippedItem("main_hand", "weapon.iron_sword"),
                new EquippedItem("off_hand", "armor.heater_shield"),
                new EquippedItem("body", "armor.leather_jerkin"),
            }, bundled.Presentation, false);
            Assert.That(armed.Attachments, Is.EquivalentTo(new[] { "HeaterShield", "IronSword" }));
            Assert.That(armed.Garments, Is.EquivalentTo(new[] { "DarkBrownHair", "LeatherJerkin" }));

            // An attachment the look named without its item is put away, an item in a slot that is not
            // its attachment's socket shows none, and an item without an attachment shows none.
            var misplaced = EquipmentGarments.Dress(own, catalog, new[]
            {
                new EquippedItem("off_hand", "weapon.iron_sword"),
                new EquippedItem("main_hand", "weapon.training_shortbow"),
            }, bundled.Presentation, false);
            Assert.That(misplaced.Attachments, Is.Empty);
        }

        [Test]
        public void ABodyHoldsItsAttachmentsOnItsSocketsWithItsHandsClosed()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(BodyCatalogPaths[0]);
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                using (var materials = new PlayerBodyMaterials())
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
                    var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    body.transform.SetPositionAndRotation(new Vector3(3f, 0.5f, -2f), Quaternion.Euler(0f, 40f, 0f));
                    var appearance = body.GetComponent<PlayerBodyAppearance>();
                    appearance.CombineParts(meshes);
                    var unarmed = appearance.CombinedRenderer.sharedMesh;
                    appearance.ApplyLook(BodyLook.Default.WithAttachment("IronSword", true).WithAttachment("HeaterShield", true), materials);

                    Assert.That(appearance.Held.Select(attachment => attachment.Name), Is.EqualTo(new[] { "HeaterShield", "IronSword" }));
                    var renderer = appearance.CombinedRenderer;
                    Assert.That(renderer.sharedMesh, Is.Not.SameAs(unarmed));
                    Assert.That(renderer.bones.Select(bone => bone.name), Does.Contain("Socket_MainHand").And.Contain("Socket_OffHand"));
                    var animator = body.GetComponent<Animator>();
                    Assert.That(animator.GetLayerWeight(animator.GetLayerIndex(HumanLocomotion.MainHandGripLayer)), Is.EqualTo(1f));
                    Assert.That(animator.GetLayerWeight(animator.GetLayerIndex(HumanLocomotion.OffHandGripLayer)), Is.EqualTo(1f));

                    // In a jog pose every vertex of an attachment lies where its model puts it on the socket.
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.SetBool(HumanLocomotion.MovingParameter, true);
                    animator.SetFloat(HumanLocomotion.SpeedParameter, HumanLocomotion.JogSpeed);
                    for (var frame = 0; frame < 12; frame++)
                    {
                        animator.Update(1f / 30f);
                    }

                    var baked = new Mesh();
                    renderer.BakeMesh(baked, true);
                    var toWorld = renderer.transform.localToWorldMatrix;
                    var actual = baked.vertices.Select(vertex => toWorld.MultiplyPoint3x4(vertex)).ToArray();
                    UnityEngine.Object.DestroyImmediate(baked);
                    foreach (var attachment in appearance.Held)
                    {
                        var socket = body.GetComponentsInChildren<Transform>().Single(bone => bone.name == attachment.Bone);
                        foreach (var expected in ModelVertices(attachment.Model, socket.localToWorldMatrix))
                        {
                            var nearest = actual.Min(vertex => (vertex - expected).sqrMagnitude);
                            Assert.That(Mathf.Sqrt(nearest), Is.LessThan(0.001f), attachment.Name + " at " + expected);
                        }
                    }

                    // Holding nothing again, the hands open and the body draws without them.
                    appearance.ApplyLook(BodyLook.Default, materials);
                    Assert.That(appearance.Held, Is.Empty);
                    Assert.That(renderer.sharedMesh, Is.SameAs(unarmed));
                    Assert.That(animator.GetLayerWeight(animator.GetLayerIndex(HumanLocomotion.MainHandGripLayer)), Is.EqualTo(0f));
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void ThePresentationCatalogRejectsABrokenAttachmentName()
        {
            var error = Assert.Throws<ItemPresentationCatalogValidationException>(() => ItemPresentationCatalogCompiler.Compile(
                new string('a', 64),
                new[] { "test.a" },
                new[]
                {
                    new ItemPresentationEntry
                    {
                        definitionId = "test.a",
                        iconAddress = string.Empty,
                        localizationKey = "items.test.a.name",
                        fallbackDisplayName = "test.a",
                        prefabPresentationKey = string.Empty,
                        attachment = "Iron Sword",
                    },
                }));

            Assert.That(error.Errors, Is.EqualTo(new[] { "entries[0].attachment must name an attachment with letters and digits." }));
        }

        private string AttachmentOf(string definitionId)
        {
            Assert.That(bundled.Presentation.TryGet(definitionId, out var entry), Is.True, definitionId);
            return entry.attachment;
        }

        // A sample of the model's vertices, the farthest from its grip first, placed by a socket.
        private static IEnumerable<Vector3> ModelVertices(GameObject model, Matrix4x4 socketToWorld)
        {
            var points = new List<Vector3>();
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                var toModel = model.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                points.AddRange(filter.sharedMesh.vertices.Select(vertex => toModel.MultiplyPoint3x4(vertex)));
            }

            return points.OrderByDescending(point => point.sqrMagnitude).Take(4)
                .Concat(points.Where((point, index) => index % 97 == 0))
                .Select(point => socketToWorld.MultiplyPoint3x4(point));
        }
    }
}
