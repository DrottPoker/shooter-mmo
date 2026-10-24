using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // A body puts itself together with the garments its look wears, as
    // docs/EQUIPMENT_VISUALS_DESIGN.md describes under Putting A Character Together In Unity.
    public sealed class PlayerBodyGarmentTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";
        private const string Jerkin = "LeatherJerkin";
        private const string Trousers = "WoolTrousers";

        [Test]
        public void AWornGarmentTakesThePlaceOfTheBodyFacesItCovers()
        {
            Wear((appearance, meshes, materials) =>
            {
                var catalog = appearance.Catalog;
                var jerkin = catalog.GarmentNamed(Jerkin);
                Assert.That(jerkin, Is.Not.Null);
                appearance.CombineParts(meshes);
                var bare = TriangleCount(appearance.CombinedRenderer.sharedMesh);

                appearance.ApplyLook(BodyLook.Default.WithGarment(Jerkin, true), materials);
                Assert.That(appearance.Worn.Select(garment => garment.Name), Is.EqualTo(new[] { Jerkin }));
                var dressed = appearance.CombinedRenderer.sharedMesh;
                var covered = new HashSet<int>(jerkin.Covered);
                var hidden = catalog.Parts.Sum(part => part.TriangleFaces.Count(face => covered.Contains(face)));
                var garmentTriangles = TriangleCount(jerkin.Model.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh);
                Assert.That(hidden, Is.GreaterThan(0));
                Assert.That(TriangleCount(dressed), Is.EqualTo(bare - hidden + garmentTriangles));
                var drawn = appearance.CombinedRenderer.sharedMaterials.Select(material => material.name).ToArray();
                Assert.That(drawn, Does.Contain("M_Garment_" + Jerkin));
                Assert.That(drawn, Does.Not.Contain("M_Garment_" + jerkin.Template));
                // Every bone of the combined mesh is the body's own.
                Assert.That(appearance.CombinedRenderer.bones.All(bone => bone.IsChildOf(appearance.transform)), Is.True);
                // No vertex is left that no triangle uses.
                var used = new HashSet<int>(dressed.triangles);
                Assert.That(used.Count, Is.EqualTo(dressed.vertexCount));

                appearance.ApplyLook(BodyLook.Default, materials);
                Assert.That(TriangleCount(appearance.CombinedRenderer.sharedMesh), Is.EqualTo(bare));
            });
        }

        [Test]
        public void BodiesWearingTheSameGarmentsShareOneMesh()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                using (var materials = new PlayerBodyMaterials())
                {
                    var look = BodyLook.Default.WithGarment(Jerkin, true);
                    var bodies = Enumerable.Range(0, 3).Select(_ => InstantiateBody(scene).GetComponent<PlayerBodyAppearance>()).ToArray();
                    bodies[0].ApplyLook(look, materials);
                    bodies[1].ApplyLook(look, materials);
                    foreach (var body in bodies)
                    {
                        body.CombineParts(meshes);
                    }

                    Assert.That(bodies[0].CombinedRenderer.sharedMesh, Is.SameAs(bodies[1].CombinedRenderer.sharedMesh));
                    Assert.That(bodies[2].CombinedRenderer.sharedMesh, Is.Not.SameAs(bodies[0].CombinedRenderer.sharedMesh));
                    Assert.That(meshes.Count, Is.EqualTo(2));
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void AGarmentOfAHigherLayerHidesTheFacesOfALowerOneUnderIt()
        {
            Wear((appearance, meshes, materials) =>
            {
                var parts = Parts(appearance);
                var jerkin = appearance.Catalog.GarmentNamed(Jerkin);
                var under = appearance.Catalog.GarmentNamed(Trousers);
                Assert.That(under.Layer, Is.LessThan(jerkin.Layer));
                var alone = PlayerBodyMeshes.Build(parts, appearance.transform, appearance.Catalog, new[] { under });
                var both = PlayerBodyMeshes.Build(parts, appearance.transform, appearance.Catalog, new[] { under, jerkin });
                try
                {
                    // The trousers' faces over the body faces the jerkin covers at the waist go, and
                    // so do the body faces the jerkin covers beyond the trousers.
                    var covered = new HashSet<int>(jerkin.Covered);
                    var underCovered = new HashSet<int>(under.Covered);
                    var trousersLost = under.Underlay.Count(face => covered.Contains(face));
                    var bodyLost = appearance.Catalog.Parts.Sum(part =>
                        part.TriangleFaces.Count(face => covered.Contains(face) && !underCovered.Contains(face)));
                    var garmentTriangles = jerkin.Underlay.Count;
                    Assert.That(trousersLost, Is.GreaterThan(0));
                    Assert.That(TriangleCount(both.Mesh),
                        Is.EqualTo(TriangleCount(alone.Mesh) - trousersLost - bodyLost + garmentTriangles));
                }
                finally
                {
                    Object.DestroyImmediate(alone.Mesh);
                    Object.DestroyImmediate(both.Mesh);
                }
            });
        }

        [Test]
        public void AGarmentThatHidesASlotLeavesTheGarmentsOfThatSlotUnderItOut()
        {
            Wear((appearance, meshes, materials) =>
            {
                var parts = Parts(appearance);
                var jerkin = appearance.Catalog.GarmentNamed(Jerkin);
                var trousers = appearance.Catalog.GarmentNamed(Trousers);
                // A floor-length robe, a chest piece that hides the legs.
                var robe = CopyOf(jerkin, "Robe", jerkin.Slot, 300, new[] { trousers.Slot });
                Assert.That(PlayerBodyMeshes.Drawn(new[] { trousers, robe }).Select(garment => garment.Name), Is.EqualTo(new[] { "Robe" }));
                // Leg armor over the robe stays.
                var greaves = CopyOf(trousers, "Greaves", trousers.Slot, robe.Layer + 100, new string[0]);
                Assert.That(PlayerBodyMeshes.Drawn(new[] { robe, greaves }).Select(garment => garment.Name),
                    Is.EqualTo(new[] { "Robe", "Greaves" }));
                var dressed = PlayerBodyMeshes.Build(parts, appearance.transform, appearance.Catalog, new[] { trousers, robe });
                var robeAlone = PlayerBodyMeshes.Build(parts, appearance.transform, appearance.Catalog, new[] { robe });
                try
                {
                    Assert.That(TriangleCount(dressed.Mesh), Is.EqualTo(TriangleCount(robeAlone.Mesh)));
                }
                finally
                {
                    Object.DestroyImmediate(dressed.Mesh);
                    Object.DestroyImmediate(robeAlone.Mesh);
                }
            });
        }

        [Test]
        public void APulledInVertexMovesInwardAlongItsNormal()
        {
            Wear((appearance, meshes, materials) =>
            {
                var parts = Parts(appearance);
                var catalog = appearance.Catalog;
                var jerkin = catalog.GarmentNamed(Jerkin);
                // A visible vertex of the neck, below the collar.
                var neck = parts.Single(part => part.name.EndsWith("_Neck"));
                var map = catalog.PartNamed(neck.name);
                var covered = new HashSet<int>(jerkin.Covered);
                var mesh = neck.sharedMesh;
                var triangles = mesh.triangles;
                var visibleVertex = -1;
                for (var triangle = 0; triangle < map.TriangleFaces.Count && visibleVertex < 0; triangle++)
                {
                    if (!covered.Contains(map.TriangleFaces[triangle]))
                    {
                        visibleVertex = triangles[triangle * 3];
                    }
                }

                const float distance = 0.002f;
                var pulled = new BodyAppearanceCatalog.Garment(jerkin.Name, jerkin.Template, jerkin.Material, jerkin.Slot, jerkin.Layer,
                    jerkin.Hides.ToArray(), jerkin.HidesUnderwear, jerkin.Hair, jerkin.Cloth, jerkin.Model, jerkin.Covered.ToArray(),
                    jerkin.Underlay.ToArray(), new[] { map.VertexSources[visibleVertex] }, new[] { distance });
                var combined = PlayerBodyMeshes.Build(parts, appearance.transform, catalog, new[] { pulled });
                try
                {
                    var toRoot = appearance.transform.worldToLocalMatrix * neck.transform.localToWorldMatrix;
                    var expected = toRoot.MultiplyPoint3x4(mesh.vertices[visibleVertex] - mesh.normals[visibleVertex].normalized * distance);
                    var original = toRoot.MultiplyPoint3x4(mesh.vertices[visibleVertex]);
                    var positions = combined.Mesh.vertices;
                    Assert.That(positions.Min(position => (position - expected).magnitude), Is.LessThan(1e-5f));
                    Assert.That(positions.Min(position => (position - original).magnitude), Is.GreaterThan(distance * 0.5f));
                }
                finally
                {
                    Object.DestroyImmediate(combined.Mesh);
                }
            });
        }

        [Test]
        public void ABodyWearsOneGarmentPerSlot()
        {
            Wear((appearance, meshes, materials) =>
            {
                var catalog = appearance.Catalog;
                Assert.That(catalog.OthersInSlot(catalog.GarmentNamed(Jerkin)).Select(garment => garment.Name),
                    Is.EqualTo(new[] { "LinenLongShirt", "LinenShirt", "NetShirt", "WoolRobe" }));
                Assert.That(catalog.OthersInSlot(catalog.GarmentNamed(Trousers)), Is.Empty);

                // Two chest pieces in the look: the first in the catalog is worn, with the trousers
                // of their own slot.
                appearance.ApplyLook(BodyLook.Default.WithGarment("LinenShirt", true).WithGarment(Jerkin, true)
                    .WithGarment(Trousers, true), materials);
                Assert.That(appearance.Worn.Select(garment => garment.Name), Is.EqualTo(new[] { Trousers, Jerkin }));
            });
        }

        [Test]
        public void LooseClothHangsOnItsChains()
        {
            Assert.That(PlayerBodyAppearance.ChainsOf("robe"), Is.EqualTo(ClothChainKind.Skirt));
            Assert.That(PlayerBodyAppearance.ChainsOf("cloak"), Is.EqualTo(ClothChainKind.Cloak));
            Assert.That(PlayerBodyAppearance.ChainsOf("hood"), Is.EqualTo(ClothChainKind.Hood));
            Assert.That(PlayerBodyAppearance.ChainsOf(string.Empty), Is.Null);
        }

        private static BodyAppearanceCatalog.Garment CopyOf(BodyAppearanceCatalog.Garment garment, string name, string slot, int layer,
            IEnumerable<string> hides)
        {
            return new BodyAppearanceCatalog.Garment(name, garment.Template, garment.Material, slot, layer, hides.ToArray(),
                garment.HidesUnderwear, garment.Hair, garment.Cloth, garment.Model, garment.Covered.ToArray(), garment.Underlay.ToArray(),
                garment.PullInVertices.ToArray(), garment.PullInDistances.ToArray());
        }

        private static void Wear(System.Action<PlayerBodyAppearance, PlayerBodyMeshes, PlayerBodyMaterials> test)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                using (var materials = new PlayerBodyMaterials())
                {
                    test(InstantiateBody(scene).GetComponent<PlayerBodyAppearance>(), meshes, materials);
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static SkinnedMeshRenderer[] Parts(PlayerBodyAppearance appearance)
        {
            return appearance.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(part => part.name != PlayerBodyAppearance.CombinedRendererName && part.sharedMesh != null)
                .ToArray();
        }

        private static int TriangleCount(Mesh mesh)
        {
            var count = 0;
            for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                count += (int)mesh.GetIndexCount(submesh) / 3;
            }

            return count;
        }

        private static GameObject InstantiateBody(UnityEngine.SceneManagement.Scene scene)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
            return (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        }
    }
}
