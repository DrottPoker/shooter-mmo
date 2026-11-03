using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Worlds;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.EditMode
{
    // The World Builder makes a World's water from shapes: a lake fills its shore at its level, a
    // river that ends in a lake lies at the lake's level and stands still there, a waterfall falls
    // from a river's end into the water below, an incomplete shape shows nothing, and new water
    // gets an id and a name of its own and stays off static batching. Its validation finds water
    // that meets with a seam, and the water follows a shape while it is edited. Every test builds
    // in a preview scene, which is never saved, so its meshes stay in memory and no asset is written.
    public sealed class WorldBuilderTests
    {
        private const string MeshDirectory = "Assets/WorldBuilderTestMeshes";

        private Scene scene;

        [SetUp]
        public void CreateScene()
        {
            scene = EditorSceneManager.NewPreviewScene();
        }

        [TearDown]
        public void CloseScene()
        {
            foreach (var filter in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MeshFilter>(true)))
            {
                if (filter.sharedMesh != null && !AssetDatabase.Contains(filter.sharedMesh))
                {
                    Object.DestroyImmediate(filter.sharedMesh);
                }
            }

            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset(MeshDirectory);
        }

        [Test]
        public void ALakeFillsItsShoreAtItsLevelAndReachesUnderTheBank()
        {
            var lake = WorldBuilderCreate.AddLake(scene, new Vector3(4f, 2f, -3f));

            // The default shore is 16 by 12 m around the lake's origin, and the surface's 1 m
            // cells reach 1.5 m under the bank.
            var mesh = lake.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(AssetDatabase.Contains(mesh), Is.False, "an unsaved scene keeps its meshes in memory");
            Assert.That(mesh.bounds.min.x, Is.EqualTo(-10f).Within(0.001f));
            Assert.That(mesh.bounds.max.x, Is.EqualTo(10f).Within(0.001f));
            Assert.That(mesh.bounds.min.z, Is.EqualTo(-8f).Within(0.001f));
            Assert.That(mesh.bounds.max.z, Is.EqualTo(8f).Within(0.001f));
            var flows = new List<Vector2>();
            mesh.GetUVs(1, flows);
            var colors = mesh.colors;
            var vertices = mesh.vertices;
            for (var index = 0; index < vertices.Length; index++)
            {
                Assert.That(lake.transform.TransformPoint(vertices[index]).y, Is.EqualTo(2f).Within(0.001f), "level");
                Assert.That(flows[index], Is.EqualTo(Vector2.zero), "still water");
                Assert.That(colors[index].r + colors[index].g + colors[index].b, Is.Zero, "no foam, waves, or foam lines");
            }

            Assert.That(lake.Body.Kind, Is.EqualTo(WaterBodyKind.Lake));
        }

        [Test]
        public void ARiverThatEndsInALakeLiesAtItsLevelAndStandsStillThere()
        {
            WorldBuilderCreate.AddLake(scene, Vector3.zero);
            var river = WorldBuilderCreate.AddRiver(scene, Vector3.zero);
            SetPoints(
                river,
                new RiverPoint(new Vector3(0f, 1f, -30f), 4f, 1f, 1f, 0.3f),
                new RiverPoint(new Vector3(0f, 0.2f, -2f), 4f, 1f, 1f, 0.3f));
            WorldBuilder.Rebuild(scene);

            var mesh = river.GetComponent<MeshFilter>().sharedMesh;
            var vertices = mesh.vertices;
            var colors = mesh.colors;
            var flows = new List<Vector2>();
            mesh.GetUVs(1, flows);
            const int columns = RiverSurfaceBuilder.Columns;
            for (var index = vertices.Length - columns; index < vertices.Length; index++)
            {
                Assert.That(vertices[index].y, Is.EqualTo(0f).Within(0.001f), "level in the lake");
                Assert.That(flows[index].magnitude, Is.LessThan(0.001f), "still in the lake");
                Assert.That(colors[index].r, Is.Zero, "no foam in the lake");
            }

            // Far up the river the water flows at its speed in the middle.
            Assert.That(vertices[columns / 2].y, Is.EqualTo(1f).Within(0.001f), "level at the start");
            Assert.That(flows[columns / 2].magnitude, Is.EqualTo(1f).Within(0.01f), "speed at the start");
            Assert.That(WorldBuilder.Validate(scene), Is.Empty);
        }

        [Test]
        public void AWaterfallFallsFromTheRiversEndIntoTheWaterBelow()
        {
            var lake = WorldBuilderCreate.AddLake(scene, Vector3.zero);
            var river = FallingRiver();
            var waterfall = WorldBuilderCreate.AddWaterfall(river);
            Assert.That(waterfall.Landing, Is.EqualTo(lake.Body), "the water below the river's end");
            WorldBuilder.Rebuild(scene);

            var fall = waterfall.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(fall.bounds.max.y, Is.EqualTo(3f).Within(0.01f), "top at the river's surface");
            Assert.That(fall.bounds.min.y, Is.InRange(-2f, 0.01f), "down to the lake");
            Assert.That(waterfall.transform.Find(WaterfallBuilder.BoilName), Is.Not.Null, "white water where it lands");
            Assert.That(lake.GetComponent<MeshFilter>().sharedMesh.colors.Max(color => color.r), Is.EqualTo(1f).Within(0.001f), "the lake churns");

            // The river's row on the lip is as wide as the river, without its bank reach.
            var vertices = river.GetComponent<MeshFilter>().sharedMesh.vertices;
            const int columns = RiverSurfaceBuilder.Columns;
            var onLip = vertices.Length - (2 * columns);
            Assert.That(Vector3.Distance(vertices[onLip], vertices[onLip + columns - 1]), Is.EqualTo(4f).Within(0.001f));
            Assert.That(WorldBuilder.Validate(scene), Is.Empty);
        }

        [Test]
        public void AWaterfallWithoutALandingShowsNothingUntilItHasOne()
        {
            WorldBuilderCreate.AddLake(scene, Vector3.zero);
            var waterfall = WorldBuilderCreate.AddWaterfall(FallingRiver());
            WorldBuilder.Rebuild(scene);
            Assert.That(waterfall.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.GreaterThan(0));

            var serialized = new SerializedObject(waterfall);
            serialized.FindProperty("landing").objectReferenceValue = null;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(WorldBuilder.Rebuild(scene), Is.True);

            Assert.That(waterfall.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.Zero);
            Assert.That(waterfall.transform.Find(WaterfallBuilder.BoilName), Is.Null);
            var issue = WorldBuilder.Validate(scene).Single();
            Assert.That(issue.Target, Is.EqualTo(waterfall));
            Assert.That(issue.Severity, Is.EqualTo(WorldBuilderIssueSeverity.Error));
        }

        // A disabled river makes no water to fall from, so its waterfall shows nothing, and the
        // validation says why instead of letting the bake go on without the waterfall.
        [Test]
        public void AWaterfallFromADisabledRiverIsAnError()
        {
            WorldBuilderCreate.AddLake(scene, Vector3.zero);
            var river = FallingRiver();
            var waterfall = WorldBuilderCreate.AddWaterfall(river);
            WorldBuilder.Rebuild(scene);

            river.enabled = false;
            WorldBuilder.Rebuild(scene);

            Assert.That(waterfall.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.Zero);
            var issues = WorldBuilder.Validate(scene);
            Assert.That(
                issues.Any(issue => issue.Target == waterfall
                    && issue.Severity == WorldBuilderIssueSeverity.Error
                    && issue.Message.Contains(river.name)),
                Is.True,
                string.Join("\n", issues));
            Assert.That(
                issues.Any(issue => issue.Target == river && issue.Severity == WorldBuilderIssueSeverity.Warning),
                Is.True,
                "the disabled river is named");
        }

        // A river that starts in a sea calms the sea there, as one that ends in it does, so they
        // meet without a seam.
        [Test]
        public void ASeaCalmsWhereARiverStartsInIt()
        {
            var sea = WorldBuilderCreate.AddSea(scene, Vector3.zero);
            var river = WorldBuilderCreate.AddRiver(scene, Vector3.zero);
            SetPoints(
                river,
                new RiverPoint(new Vector3(0f, 0.1f, 10f), 4f, 1f, 1f, 0.3f),
                new RiverPoint(new Vector3(0f, 2f, 50f), 4f, 1f, 1f, 0.3f));
            WorldBuilder.Rebuild(scene);

            var mesh = sea.GetComponent<MeshFilter>().sharedMesh;
            var vertices = mesh.vertices;
            var colors = mesh.colors;
            var near = 0;
            for (var index = 0; index < vertices.Length; index++)
            {
                var world = sea.transform.TransformPoint(vertices[index]);
                if (Vector2.Distance(new Vector2(world.x, world.z), new Vector2(0f, 12f)) <= 2f)
                {
                    near++;
                    Assert.That(colors[index].g, Is.Zero, "waves at " + world);
                    Assert.That(colors[index].b, Is.Zero, "foam lines at " + world);
                }
            }

            Assert.That(near, Is.GreaterThan(0));
            Assert.That(WorldBuilder.Validate(scene), Is.Empty);
        }

        [Test]
        public void NewWaterGetsAnIdAndANameOfItsOwnBelowTheCollisionRootAndStaysOffStaticBatching()
        {
            var world = new GameObject("World");
            SceneManager.MoveGameObjectToScene(world, scene);
            world.AddComponent<WorldCollisionAuthoring>();

            var first = WorldBuilderCreate.AddLake(scene, Vector3.zero);
            var second = WorldBuilderCreate.AddLake(scene, new Vector3(40f, 0f, 0f));

            Assert.That(first.Body.BodyId, Is.EqualTo("lake"));
            Assert.That(second.Body.BodyId, Is.EqualTo("lake-2"));
            Assert.That(first.name, Is.EqualTo("Lake"));
            Assert.That(second.name, Is.EqualTo("Lake 2"));
            foreach (var lake in new[] { first, second })
            {
                Assert.That(lake.transform.parent.name, Is.EqualTo(WorldBuilderCreate.WaterGroupName));
                Assert.That(lake.transform.parent.parent, Is.EqualTo(world.transform));
                Assert.That(GameObjectUtility.AreStaticEditorFlagsSet(lake.gameObject, StaticEditorFlags.BatchingStatic), Is.False);
                Assert.That(GameObjectUtility.AreStaticEditorFlagsSet(lake.gameObject, StaticEditorFlags.ContributeGI), Is.True);
                Assert.That(lake.GetComponent<MeshRenderer>().sharedMaterial.shader.name, Is.EqualTo(WaterAssets.ShaderName));
            }

            Assert.That(WorldBuilder.Validate(scene), Is.Empty);
        }

        [Test]
        public void TwoLakesThatOverlapAtDifferentLevelsMeetWithASeam()
        {
            var low = WorldBuilderCreate.AddLake(scene, Vector3.zero);
            WorldBuilderCreate.AddLake(scene, new Vector3(10f, 0.1f, 0f));

            var issues = WorldBuilder.Validate(scene);

            Assert.That(issues, Is.Not.Empty);
            Assert.That(issues.All(issue => issue.Severity == WorldBuilderIssueSeverity.Error), Is.True);
            Assert.That(issues.Any(issue => issue.Target == low.Body && issue.Message.Contains("lie at different levels")), Is.True);
        }

        [Test]
        public void ARebuildWritesOnlyWhatChanged()
        {
            var lake = WorldBuilderCreate.AddLake(scene, Vector3.zero);
            WorldBuilderCreate.AddSea(scene, new Vector3(60f, -1f, 0f));
            Assert.That(WorldBuilder.Rebuild(scene), Is.False, "nothing changed since the shapes were added");

            var serialized = new SerializedObject(lake);
            serialized.FindProperty("shore").GetArrayElementAtIndex(0).vector2Value = new Vector2(-12f, -6f);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(WorldBuilder.Rebuild(scene), Is.True, "the shore moved");
            Assert.That(lake.GetComponent<MeshFilter>().sharedMesh.bounds.min.x, Is.EqualTo(-13f).Within(0.001f));
            Assert.That(WorldBuilder.Rebuild(scene), Is.False, "nothing changed since the last rebuild");
        }

        // While a shape is edited, its water follows a moment later, without a rebuild by hand.
        [UnityTest]
        public IEnumerator TheWaterFollowsAShapeWhileItIsEdited()
        {
            var lake = WorldBuilderCreate.AddLake(scene, Vector3.zero);
            var serialized = new SerializedObject(lake);
            serialized.FindProperty("shore").GetArrayElementAtIndex(0).vector2Value = new Vector2(-12f, -6f);
            serialized.ApplyModifiedProperties();

            var mesh = lake.GetComponent<MeshFilter>().sharedMesh;
            var started = EditorApplication.timeSinceStartup;
            while (mesh.bounds.min.x > -12.9f && EditorApplication.timeSinceStartup - started < 5d)
            {
                yield return null;
            }

            Assert.That(mesh.bounds.min.x, Is.EqualTo(-13f).Within(0.001f));
        }

        // A shape copied from another scene still holds that scene's mesh asset; a rebuild gives
        // it a mesh of its own and leaves the asset as it is.
        [Test]
        public void AShapeThatHoldsAnotherScenesMeshGetsAMeshOfItsOwn()
        {
            AssetDatabase.CreateFolder("Assets", MeshDirectory.Substring("Assets/".Length));
            var shared = new Mesh { vertices = new[] { Vector3.zero, Vector3.forward, Vector3.right }, triangles = new[] { 0, 1, 2 } };
            AssetDatabase.CreateAsset(shared, MeshDirectory + "/Shared.asset");
            var lake = WorldBuilderCreate.AddLake(scene, Vector3.zero);
            lake.GetComponent<MeshFilter>().sharedMesh = shared;

            Assert.That(WorldBuilder.Rebuild(scene), Is.True);

            var own = lake.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(own, Is.Not.EqualTo(shared));
            Assert.That(own.vertexCount, Is.GreaterThan(3));
            Assert.That(shared.vertexCount, Is.EqualTo(3), "the other scene's mesh stays as it was");
        }

        // A copied lake that loses its shore before its first rebuild lets go of the other
        // scene's mesh instead of emptying it.
        [Test]
        public void AnIncompleteShapeLetsGoOfAnotherScenesMesh()
        {
            AssetDatabase.CreateFolder("Assets", MeshDirectory.Substring("Assets/".Length));
            var shared = new Mesh { vertices = new[] { Vector3.zero, Vector3.forward, Vector3.right }, triangles = new[] { 0, 1, 2 } };
            AssetDatabase.CreateAsset(shared, MeshDirectory + "/Shared.asset");
            var lake = WorldBuilderCreate.AddLake(scene, Vector3.zero);
            var ownMesh = lake.GetComponent<MeshFilter>().sharedMesh;
            lake.GetComponent<MeshFilter>().sharedMesh = shared;
            Object.DestroyImmediate(ownMesh);
            var serialized = new SerializedObject(lake);
            serialized.FindProperty("shore").arraySize = 2;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(WorldBuilder.Rebuild(scene), Is.True);

            Assert.That(lake.GetComponent<MeshFilter>().sharedMesh, Is.Null);
            Assert.That(shared.vertexCount, Is.EqualTo(3), "the other scene's mesh stays as it was");
        }

        [Test]
        public void ABuildWritesOnlyMeshesItsSceneOwns()
        {
            AssetDatabase.CreateFolder("Assets", MeshDirectory.Substring("Assets/".Length));
            var asset = new Mesh();
            AssetDatabase.CreateAsset(asset, MeshDirectory + "/Owned.asset");
            var inMemory = new Mesh();
            var nowhere = new HashSet<string>();
            var elsewhere = new HashSet<string> { MeshDirectory + "/Owned.asset" };
            try
            {
                Assert.That(WorldBuilderAssets.MayWrite(asset, true, nowhere), Is.True, "a saved scene's own asset");
                Assert.That(WorldBuilderAssets.MayWrite(asset, true, elsewhere), Is.False, "an asset another scene uses too");
                Assert.That(WorldBuilderAssets.MayWrite(asset, false, nowhere), Is.False, "an asset, from a scene never saved");
                Assert.That(WorldBuilderAssets.MayWrite(inMemory, false, nowhere), Is.True, "a mesh of a scene never saved");
                Assert.That(WorldBuilderAssets.MayWrite(inMemory, true, nowhere), Is.False, "a mesh that a saved scene keeps in itself");
            }
            finally
            {
                Object.DestroyImmediate(inMemory);
            }
        }

        // The sea's grid lies on the World's west edge or 0.75 m off it, and turned or not, so the
        // edge runs along its cells' sides or crosses them; the surface still ends exactly at the
        // edge, where the sea beyond the World starts, without a triangle that has no area.
        [TestCase(0f, 0f)]
        [TestCase(0f, 45f)]
        [TestCase(0.75f, 0f)]
        [TestCase(0.75f, 30f)]
        public void ASeaEndsExactlyAtTheWorldsEdgeHoweverItsGridLies(float offset, float yaw)
        {
            var world = new GameObject("World");
            SceneManager.MoveGameObjectToScene(world, scene);
            world.AddComponent<WorldCollisionAuthoring>();
            var ground = new GameObject("Ground");
            ground.transform.SetParent(world.transform, false);
            var box = ground.AddComponent<BoxCollider>();
            box.center = new Vector3(20f, -0.5f, 0f);
            box.size = new Vector3(40f, 1f, 60f);
            var sea = WorldBuilderCreate.AddSea(scene, new Vector3(offset, -0.2f, 0f));
            sea.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            var serialized = new SerializedObject(sea);
            serialized.FindProperty("horizon").intValue = (int)HorizonSides.West;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            WorldBuilder.Rebuild(scene);

            var mesh = sea.GetComponent<MeshFilter>().sharedMesh;
            var westmost = mesh.vertices.Min(vertex => sea.transform.TransformPoint(vertex).x);
            Assert.That(westmost, Is.EqualTo(0f).Within(0.0001f));
            var colors = mesh.colors;
            var vertices = mesh.vertices;
            for (var index = 0; index < vertices.Length; index++)
            {
                if (sea.transform.TransformPoint(vertices[index]).x < 0.0001f)
                {
                    Assert.That(colors[index].g, Is.LessThan(0.00001f), "no waves where the sea beyond the World starts");
                }
            }

            var triangles = mesh.triangles;
            for (var index = 0; index < triangles.Length; index += 3)
            {
                var a = vertices[triangles[index]];
                var cross = Vector3.Cross(vertices[triangles[index + 1]] - a, vertices[triangles[index + 2]] - a);
                Assert.That(cross.y, Is.GreaterThan(0.00001f), "every triangle faces up and has an area");
            }

            // The default outline is 40 m square around the sea's origin, so the water covers the
            // edge from 15 m south to 15 m north of it, without a gap.
            for (var z = -15f; z <= 15f; z += 0.5f)
            {
                Assert.That(Covers(mesh, sea.transform, new Vector2(0.01f, z)), Is.True, "water at the edge, " + z + " m north");
            }
        }

        // A lake moved into another open scene keeps its mesh below the scene it came from; that
        // mesh is in use while the other scene holds it, saved or not.
        [Test]
        public void AMeshThatAnotherOpenSceneHoldsIsNotUnused()
        {
            AssetDatabase.CreateFolder("Assets", MeshDirectory.Substring("Assets/".Length));
            var moved = new Mesh();
            AssetDatabase.CreateAsset(moved, MeshDirectory + "/Moved.asset");
            var lake = WorldBuilderCreate.AddLake(scene, Vector3.zero);
            var ownMesh = lake.GetComponent<MeshFilter>().sharedMesh;
            lake.GetComponent<MeshFilter>().sharedMesh = moved;
            Object.DestroyImmediate(ownMesh);

            var unused = WorldBuilderAssets.UnusedMeshes(MeshDirectory, WorldBuilderAssets.MeshesOf(new[] { scene }), null);

            Assert.That(unused, Is.Empty);
        }

        // A waterfall that lands in a river that falls ends at the river's surface where the
        // water lands, not at the height of the river's end.
        [Test]
        public void AWaterfallIntoARiverEndsAtTheRiversSurfaceWhereItLands()
        {
            var below = WorldBuilderCreate.AddRiver(scene, Vector3.zero);
            SetPoints(
                below,
                new RiverPoint(new Vector3(0f, 2f, 0f), 4f, 1f, 1f, 0f),
                new RiverPoint(new Vector3(0f, 0f, 40f), 4f, 1f, 1f, 0f));
            var above = WorldBuilderCreate.AddRiver(scene, Vector3.zero);
            SetPoints(
                above,
                new RiverPoint(new Vector3(-10f, 5f, 20f), 2f, 0.5f, 1f, 0f),
                new RiverPoint(new Vector3(0f, 5f, 20f), 2f, 0.5f, 1f, 0f));
            var waterfall = WorldBuilderCreate.AddWaterfall(above);
            var serialized = new SerializedObject(waterfall);
            serialized.FindProperty("landing").objectReferenceValue = below.Body;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            WorldBuilder.Rebuild(scene);

            // The river below lies 1 m high halfway along, where the water lands.
            var fall = WaterScene.Collect(scene).Waterfalls.Single().Fall;
            Assert.That(fall.LandingLevel, Is.EqualTo(1f).Within(0.01f));
            var column = waterfall.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(column.bounds.min.y, Is.InRange(0.7f, 1f), "the fall ends at the river's surface");
            Assert.That(WorldBuilder.Validate(scene), Is.Empty);
        }

        [Test]
        public void AMeshThatNoShapeUsesAnyMoreIsUnused()
        {
            AssetDatabase.CreateFolder("Assets", MeshDirectory.Substring("Assets/".Length));
            var kept = new Mesh();
            var removed = new Mesh();
            AssetDatabase.CreateAsset(kept, MeshDirectory + "/Kept.asset");
            AssetDatabase.CreateAsset(removed, MeshDirectory + "/Removed.asset");

            var unused = WorldBuilderAssets.UnusedMeshes(MeshDirectory, new HashSet<Mesh> { kept }, null);

            Assert.That(unused, Is.EqualTo(new[] { removed }));
        }

        // Whether a triangle of the mesh lies over the point in world X and Z.
        private static bool Covers(Mesh mesh, Transform transform, Vector2 point)
        {
            var vertices = mesh.vertices.Select(vertex => transform.TransformPoint(vertex)).ToArray();
            var triangles = mesh.triangles;
            for (var index = 0; index < triangles.Length; index += 3)
            {
                var a = vertices[triangles[index]];
                var b = vertices[triangles[index + 1]];
                var c = vertices[triangles[index + 2]];
                var determinant = ((b.z - c.z) * (a.x - c.x)) + ((c.x - b.x) * (a.z - c.z));
                if (Mathf.Abs(determinant) < 0.000001f)
                {
                    continue;
                }

                var u = (((b.z - c.z) * (point.x - c.x)) + ((c.x - b.x) * (point.y - c.z))) / determinant;
                var v = (((c.z - a.z) * (point.x - c.x)) + ((a.x - c.x) * (point.y - c.z))) / determinant;
                if (u >= -0.0001f && v >= -0.0001f && 1f - u - v >= -0.0001f)
                {
                    return true;
                }
            }

            return false;
        }

        // A river that ends 3 m above the default lake around the origin, 2 m inside its shore.
        private RiverShape FallingRiver()
        {
            var river = WorldBuilderCreate.AddRiver(scene, Vector3.zero);
            SetPoints(
                river,
                new RiverPoint(new Vector3(0f, 3f, -30f), 4f, 1f, 1f, 0f),
                new RiverPoint(new Vector3(0f, 3f, -4f), 4f, 1f, 1f, 0f));
            return river;
        }

        private static void SetPoints(RiverShape river, params RiverPoint[] points)
        {
            var serialized = new SerializedObject(river);
            var list = serialized.FindProperty("points");
            list.arraySize = points.Length;
            for (var index = 0; index < points.Length; index++)
            {
                var element = list.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("Position").vector3Value = points[index].Position;
                element.FindPropertyRelative("Width").floatValue = points[index].Width;
                element.FindPropertyRelative("Depth").floatValue = points[index].Depth;
                element.FindPropertyRelative("Speed").floatValue = points[index].Speed;
                element.FindPropertyRelative("Foam").floatValue = points[index].Foam;
            }

            serialized.FindProperty("curved").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
