using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Editor;
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
        private const string Shirt = "LinenShirt";
        private const string Trousers = "WoolTrousers";
        private const string Hair = "DarkBrownHair";
        private const string HoodUp = "WoolHoodUp";
        private const string HoodDown = "WoolHoodDown";

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
                var garmentTriangles = TriangleCount(PlayerBodyMeshes.ModelOf(jerkin, meshes.Content).GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh);
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
                using (var content = Loaded())
                using (var meshes = new PlayerBodyMeshes(false, content))
                using (var materials = new PlayerBodyMaterials(content))
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
                // The plain shirt covers body faces under the trousers' waistband; the jerkin's own
                // masks cover none of them on the rebuilt male body.
                var shirt = appearance.Catalog.GarmentNamed(Shirt);
                var under = appearance.Catalog.GarmentNamed(Trousers);
                Assert.That(under.Layer, Is.LessThan(shirt.Layer));
                var alone = PlayerBodyMeshes.Build(parts, appearance.transform, appearance.Catalog, new[] { under }, null, meshes.Content);
                var both = PlayerBodyMeshes.Build(parts, appearance.transform, appearance.Catalog, new[] { under, shirt }, null, meshes.Content);
                try
                {
                    // The trousers lose the triangles over body faces the shirt covers and those the
                    // shirt's data hides, each once; the body loses the faces it covers beyond them.
                    var covered = new HashSet<int>(shirt.Covered);
                    var underCovered = new HashSet<int>(under.Covered);
                    var trousersLost = under.Underlay.Count(face => covered.Contains(face));
                    var hiddenLost = shirt.HiddenTrianglesOf(under.Template)
                        .Count(triangle => !covered.Contains(under.Underlay[triangle]));
                    var bodyLost = appearance.Catalog.Parts.Sum(part =>
                        part.TriangleFaces.Count(face => covered.Contains(face) && !underCovered.Contains(face)));
                    var garmentTriangles = shirt.Underlay.Count;
                    Assert.That(trousersLost, Is.GreaterThan(0));
                    Assert.That(TriangleCount(both.Mesh),
                        Is.EqualTo(TriangleCount(alone.Mesh) - trousersLost - hiddenLost - bodyLost + garmentTriangles));
                }
                finally
                {
                    Object.DestroyImmediate(alone.Mesh);
                    Object.DestroyImmediate(both.Mesh);
                }
            });
        }

        [Test]
        public void AGarmentOfAHigherLayerHidesTheTrianglesOfALowerTemplateThatItsDataNames()
        {
            Wear((appearance, meshes, materials) =>
            {
                var parts = Parts(appearance);
                var catalog = appearance.Catalog;
                var trousers = catalog.GarmentNamed(Trousers);
                var shirt = catalog.GarmentNamed(Shirt);
                // Fixtures: of the trousers' triangles that the underlay rule leaves drawn under the
                // shirt, the shirt's data names every third as hidden.
                var covered = new HashSet<int>(shirt.Covered);
                var hidden = Enumerable.Range(0, trousers.Underlay.Count)
                    .Where(triangle => !covered.Contains(trousers.Underlay[triangle]) && triangle % 3 == 0)
                    .ToArray();
                Assert.That(hidden, Is.Not.Empty);
                var plain = WithHidden(shirt, shirt.Layer);
                var hiding = WithHidden(shirt, shirt.Layer, new BodyAppearanceCatalog.HiddenTriangles(trousers.Template, hidden));
                Assert.That(hiding.HiddenTrianglesOf(trousers.Template), Is.EqualTo(hidden));
                Assert.That(hiding.HiddenTrianglesOf("Boots"), Is.Empty);
                var counts = TriangleCounts(parts, appearance, meshes,
                    new[] { trousers },
                    new[] { trousers, plain },
                    new[] { trousers, hiding },
                    // Taken off again, the trousers are drawn in full.
                    new[] { trousers },
                    // A garment hides nothing of a template of a layer as high as its own or higher.
                    new[] { trousers, WithHidden(shirt, trousers.Layer, new BodyAppearanceCatalog.HiddenTriangles(trousers.Template, hidden)) },
                    new[] { trousers, WithHidden(shirt, trousers.Layer) },
                    // Nor of another template than the one its data names.
                    new[] { trousers, WithHidden(shirt, shirt.Layer, new BodyAppearanceCatalog.HiddenTriangles("Boots", hidden)) });
                Assert.That(counts[2], Is.EqualTo(counts[1] - hidden.Length));
                Assert.That(counts[3], Is.EqualTo(counts[0]));
                Assert.That(counts[4], Is.EqualTo(counts[5]));
                Assert.That(counts[6], Is.EqualTo(counts[1]));
            });
        }

        [Test]
        public void TwoGarmentsOfHigherLayersHideTogetherWhatEitherNames()
        {
            Wear((appearance, meshes, materials) =>
            {
                var parts = Parts(appearance);
                var catalog = appearance.Catalog;
                var trousers = catalog.GarmentNamed(Trousers);
                var shirt = catalog.GarmentNamed(Shirt);
                var boots = catalog.GarmentNamed("LeatherBoots");
                Assert.That(boots, Is.Not.Null);
                Assert.That(boots.Layer, Is.GreaterThan(trousers.Layer));
                // Fixtures: of the trousers' triangles that neither covers by its underlay, the shirt's
                // data names every second and the boots' every third, so some are named by both.
                var covered = new HashSet<int>(shirt.Covered.Concat(boots.Covered));
                var drawn = Enumerable.Range(0, trousers.Underlay.Count).Where(triangle => !covered.Contains(trousers.Underlay[triangle])).ToArray();
                var byShirt = drawn.Where(triangle => triangle % 2 == 0).ToArray();
                var byBoots = drawn.Where(triangle => triangle % 3 == 0).ToArray();
                var either = byShirt.Union(byBoots).Count();
                Assert.That(either, Is.LessThan(byShirt.Length + byBoots.Length));
                var counts = TriangleCounts(parts, appearance, meshes,
                    new[] { trousers, WithHidden(shirt, shirt.Layer), WithHidden(boots, boots.Layer) },
                    new[]
                    {
                        trousers,
                        WithHidden(shirt, shirt.Layer, new BodyAppearanceCatalog.HiddenTriangles(trousers.Template, byShirt)),
                        WithHidden(boots, boots.Layer, new BodyAppearanceCatalog.HiddenTriangles(trousers.Template, byBoots)),
                    });
                Assert.That(counts[1], Is.EqualTo(counts[0] - either));
            });
        }

        // Which triangles a garment hides is the art pipeline's decision, made in Blender: a net's data
        // names none under its holes. The body build leaves out exactly the triangles the data names,
        // so every other one, such as one under a hole, stays drawn.
        [Test]
        public void TrianglesThatAGarmentsDataDoesNotNameStayDrawnUnderIt()
        {
            Wear((appearance, meshes, materials) =>
            {
                var parts = Parts(appearance);
                var catalog = appearance.Catalog;
                var trousers = catalog.GarmentNamed(Trousers);
                var net = catalog.GarmentNamed("NetShirt");
                Assert.That(net, Is.Not.Null);
                Assert.That(net.Layer, Is.GreaterThan(trousers.Layer));
                // Fixtures: of the trousers' triangles that the underlay rule leaves drawn, the data
                // names every second one.
                var covered = new HashSet<int>(net.Covered);
                var drawn = Enumerable.Range(0, trousers.Underlay.Count).Where(triangle => !covered.Contains(trousers.Underlay[triangle])).ToArray();
                var named = drawn.Where(triangle => triangle % 2 == 0).ToArray();
                var counts = TriangleCounts(parts, appearance, meshes,
                    new[] { trousers, WithHidden(net, net.Layer) },
                    new[] { trousers, WithHidden(net, net.Layer, new BodyAppearanceCatalog.HiddenTriangles(trousers.Template, named)) });
                Assert.That(counts[1], Is.EqualTo(counts[0] - named.Length));
                Assert.That(counts[1], Is.GreaterThan(counts[0] - drawn.Length));
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
                var dressed = PlayerBodyMeshes.Build(parts, appearance.transform, appearance.Catalog, new[] { trousers, robe }, null, meshes.Content);
                var robeAlone = PlayerBodyMeshes.Build(parts, appearance.transform, appearance.Catalog, new[] { robe }, null, meshes.Content);
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
                var pulled = new BodyAppearanceCatalog.Garment(jerkin.Name, jerkin.Template, jerkin.Address, jerkin.Slot, jerkin.Layer,
                    jerkin.Hides.ToArray(), jerkin.HidesUnderwear, jerkin.Hair, jerkin.Cloth, jerkin.Covered.ToArray(),
                    jerkin.Underlay.ToArray(), new[] { map.VertexSources[visibleVertex] }, new[] { distance });
                var combined = PlayerBodyMeshes.Build(parts, appearance.transform, catalog, new[] { pulled }, null, meshes.Content);
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
        public void HeadwearPressesTheHairFlatHidesItOrLeavesIt()
        {
            Wear((appearance, meshes, materials) =>
            {
                var parts = Parts(appearance);
                var catalog = appearance.Catalog;
                var hair = catalog.GarmentNamed(Hair);
                var hoodUp = catalog.GarmentNamed(HoodUp);
                var hoodDown = catalog.GarmentNamed(HoodDown);
                Assert.That(hair.Slot, Is.EqualTo(BodyAppearanceCatalog.Garment.HairSlot));
                Assert.That(hoodUp.Hair, Is.EqualTo(BodyAppearanceCatalog.Garment.CompressHair));
                Assert.That(hoodDown.Hair, Is.EqualTo("show"));
                // A coif that hides the hair leaves it out.
                var coif = new BodyAppearanceCatalog.Garment("Coif", hoodUp.Template, hoodUp.Address, hoodUp.Slot, hoodUp.Layer,
                    hoodUp.Hides.ToArray(), hoodUp.HidesUnderwear, BodyAppearanceCatalog.Garment.HideHair, hoodUp.Cloth,
                    hoodUp.Covered.ToArray(), hoodUp.Underlay.ToArray(), hoodUp.PullInVertices.ToArray(), hoodUp.PullInDistances.ToArray());
                Assert.That(PlayerBodyMeshes.Drawn(new[] { hair, coif }).Select(garment => garment.Name), Is.EqualTo(new[] { "Coif" }));

                // The vertex the form under headwear moves the most, free and pressed flat.
                var hairModel = PlayerBodyMeshes.ModelOf(hair, meshes.Content);
                var renderer = hairModel.GetComponentInChildren<SkinnedMeshRenderer>();
                var mesh = renderer.sharedMesh;
                var shape = mesh.GetBlendShapeIndex(BodyAppearanceCatalog.Garment.UnderHeadwearShape);
                Assert.That(shape, Is.GreaterThanOrEqualTo(0));
                var deltas = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(shape, mesh.GetBlendShapeFrameCount(shape) - 1, deltas, null, null);
                var moved = Enumerable.Range(0, deltas.Length).OrderByDescending(vertex => deltas[vertex].sqrMagnitude).First();
                Assert.That(deltas[moved].magnitude, Is.GreaterThan(0.005f));
                var toRoot = hairModel.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                var free = toRoot.MultiplyPoint3x4(mesh.vertices[moved]);
                var pressed = toRoot.MultiplyPoint3x4(mesh.vertices[moved] + deltas[moved]);

                var alone = PlayerBodyMeshes.Build(parts, appearance.transform, catalog, new[] { hair }, null, meshes.Content);
                var up = PlayerBodyMeshes.Build(parts, appearance.transform, catalog, new[] { hair, hoodUp }, null, meshes.Content);
                var down = PlayerBodyMeshes.Build(parts, appearance.transform, catalog, new[] { hair, hoodDown }, null, meshes.Content);
                try
                {
                    Assert.That(Nearest(alone.Mesh, free), Is.LessThan(1e-5f));
                    Assert.That(Nearest(down.Mesh, free), Is.LessThan(1e-5f));
                    Assert.That(Nearest(up.Mesh, pressed), Is.LessThan(1e-5f));
                    Assert.That(Nearest(up.Mesh, free), Is.GreaterThan(0.002f));
                }
                finally
                {
                    Object.DestroyImmediate(alone.Mesh);
                    Object.DestroyImmediate(up.Mesh);
                    Object.DestroyImmediate(down.Mesh);
                }

                // Bodies with the hood up and down share no mesh.
                appearance.ApplyLook(BodyLook.Default.WithGarment(Hair, true).WithGarment(HoodUp, true), materials);
                appearance.CombineParts(meshes);
                var hoodUpMesh = appearance.CombinedRenderer.sharedMesh;
                appearance.ApplyLook(BodyLook.Default.WithGarment(Hair, true).WithGarment(HoodDown, true), materials);
                appearance.CombineParts(meshes);
                Assert.That(appearance.CombinedRenderer.sharedMesh, Is.Not.SameAs(hoodUpMesh));
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

        // A copy of a garment at a layer, with the triangles it hides of the templates under it.
        private static BodyAppearanceCatalog.Garment WithHidden(BodyAppearanceCatalog.Garment garment, int layer,
            params BodyAppearanceCatalog.HiddenTriangles[] hidden)
        {
            return new BodyAppearanceCatalog.Garment(garment.Name, garment.Template, garment.Address, garment.Slot, layer,
                garment.Hides.ToArray(), garment.HidesUnderwear, garment.Hair, garment.Cloth, garment.Covered.ToArray(),
                garment.Underlay.ToArray(), garment.PullInVertices.ToArray(), garment.PullInDistances.ToArray(), hidden);
        }

        // The triangles of the body put together with each set of garments.
        private static int[] TriangleCounts(SkinnedMeshRenderer[] parts, PlayerBodyAppearance appearance, PlayerBodyMeshes meshes,
            params BodyAppearanceCatalog.Garment[][] looks)
        {
            return looks.Select(garments =>
            {
                var combined = PlayerBodyMeshes.Build(parts, appearance.transform, appearance.Catalog, garments, null, meshes.Content);
                try
                {
                    return TriangleCount(combined.Mesh);
                }
                finally
                {
                    Object.DestroyImmediate(combined.Mesh);
                }
            }).ToArray();
        }

        private static BodyAppearanceCatalog.Garment CopyOf(BodyAppearanceCatalog.Garment garment, string name, string slot, int layer,
            IEnumerable<string> hides)
        {
            return new BodyAppearanceCatalog.Garment(name, garment.Template, garment.Address, slot, layer, hides.ToArray(),
                garment.HidesUnderwear, garment.Hair, garment.Cloth, garment.Covered.ToArray(), garment.Underlay.ToArray(),
                garment.PullInVertices.ToArray(), garment.PullInDistances.ToArray());
        }

        private static void Wear(System.Action<PlayerBodyAppearance, PlayerBodyMeshes, PlayerBodyMaterials> test)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var content = Loaded())
                using (var meshes = new PlayerBodyMeshes(false, content))
                using (var materials = new PlayerBodyMaterials(content))
                {
                    test(InstantiateBody(scene).GetComponent<PlayerBodyAppearance>(), meshes, materials);
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // The content of every look of the body, loaded as the tests' content helper loads it.
        private static PlayerBodyContent Loaded()
        {
            var content = EditorBodyContent.Create();
            var catalog = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath).GetComponent<PlayerBodyAppearance>().Catalog;
            Assert.That(EditorBodyContent.LoadAll(content, catalog), Is.True);
            return content;
        }

        private static SkinnedMeshRenderer[] Parts(PlayerBodyAppearance appearance)
        {
            return appearance.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(part => part.name != PlayerBodyAppearance.CombinedRendererName && part.sharedMesh != null)
                .ToArray();
        }

        private static float Nearest(Mesh mesh, Vector3 point)
        {
            return mesh.vertices.Min(position => (position - point).magnitude);
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
