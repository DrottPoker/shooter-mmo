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
