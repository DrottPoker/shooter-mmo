using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Worlds;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Tests.EditMode
{
    // The water of the World Builder carves its beds and banks into a WorldBuilderTerrain and
    // paints them with its bed layer: a lake's floor falls from its shore, a river's channel is
    // as deep as the river in its middle, a sea keeps falling toward its horizon, and a waterfall
    // digs a pool. Moving or removing the water gives the ground back, while ground changed by
    // hand away from the water stays. Every test shapes a 64 m terrain with flat ground at 10 m
    // in a preview scene, whose terrain data stays in memory unless the test saves it as an asset.
    public sealed class TerrainCarvingTests
    {
        private const float Ground = 10f;
        private const float HeightTolerance = 0.002f;
        private const string AssetDirectory = "Assets/TerrainCarvingTestAssets";

        private readonly List<Object> made = new List<Object>();
        private Scene scene;
        private GameObject world;
        private Terrain terrain;
        private TerrainLayer grass;
        private TerrainLayer sand;

        [SetUp]
        public void CreateTerrain()
        {
            scene = EditorSceneManager.NewPreviewScene();
            grass = new TerrainLayer { name = "Grass" };
            sand = new TerrainLayer { name = "Sand" };
            world = new GameObject("World");
            SceneManager.MoveGameObjectToScene(world, scene);
            world.AddComponent<WorldCollisionAuthoring>();
            terrain = CreateTerrain(Vector3.zero);
        }

        [TearDown]
        public void CloseScene()
        {
            TerrainCarver.Restore(terrain);
            foreach (var filter in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MeshFilter>(true)))
            {
                if (filter.sharedMesh != null && !AssetDatabase.Contains(filter.sharedMesh))
                {
                    Object.DestroyImmediate(filter.sharedMesh);
                }
            }

            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset(AssetDirectory);
            foreach (var item in made.Concat(new Object[] { grass, sand }))
            {
                if (item != null && !AssetDatabase.Contains(item))
                {
                    Object.DestroyImmediate(item);
                }
            }

            made.Clear();
        }

        [Test]
        public void ALakeCarvesItsFloorAndBankAndPaintsThemWithTheBedLayer()
        {
            // The default shore is 16 by 12 m, so the middle lies 6 m inside it.
            WorldBuilderCreate.AddLake(scene, new Vector3(32f, 9.6f, 32f));

            Assert.That(HeightAt(32f, 32f), Is.EqualTo(9.6f - (0.3f * 6f)).Within(HeightTolerance), "the floor falls 0.3 m per meter from the shore");
            Assert.That(HeightAt(32f, 26f), Is.EqualTo(9.6f).Within(HeightTolerance), "the floor meets the surface at the shore");
            Assert.That(HeightAt(32f, 25f), Is.EqualTo(9.8f).Within(HeightTolerance), "the bank rises to the ground over 2 m");
            Assert.That(HeightAt(32f, 23f), Is.EqualTo(Ground).Within(HeightTolerance), "beyond the bank the ground stays");
            Assert.That(SandAt(32f, 32f), Is.EqualTo(1f).Within(0.01f), "sand on the floor");
            Assert.That(SandAt(32f, 25f), Is.EqualTo(1f).Within(0.01f), "sand on the bank");
            Assert.That(SandAt(32f, 20f), Is.Zero, "grass beyond the bank");
        }

        // Without a bank, the floor still meets the surface all around the shore, also on its north
        // and east edges, which a test of whether a point lies inside the shore counts as outside.
        [Test]
        public void ALakeWithoutABankMeetsItsSurfaceAllAroundItsShore()
        {
            var lake = WorldBuilderCreate.AddLake(scene, new Vector3(32f, 9.6f, 32f));
            var serialized = new SerializedObject(lake);
            serialized.FindProperty("bankWidth").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            WorldBuilder.Rebuild(scene);

            foreach (var shore in new[] { new Vector2(32f, 26f), new Vector2(32f, 38f), new Vector2(24f, 32f), new Vector2(40f, 32f) })
            {
                Assert.That(HeightAt(shore.x, shore.y), Is.EqualTo(9.6f).Within(HeightTolerance), "the shore at " + shore);
            }
        }

        // Without a bank, a river's channel still meets its surface at both edges of its water.
        [Test]
        public void ARiverWithoutABankMeetsItsSurfaceAtBothEdges()
        {
            var river = WorldBuilderCreate.AddRiver(scene, Vector3.zero);
            var serialized = new SerializedObject(river);
            var points = serialized.FindProperty("points");
            points.arraySize = 2;
            foreach (var index in new[] { 0, 1 })
            {
                var element = points.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("Position").vector3Value = new Vector3(32f, 9.5f, index == 0 ? 4f : 60f);
                element.FindPropertyRelative("Width").floatValue = 8f;
                element.FindPropertyRelative("Depth").floatValue = 1f;
            }

            serialized.FindProperty("curved").boolValue = false;
            serialized.FindProperty("bankWidth").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            WorldBuilder.Rebuild(scene);

            Assert.That(HeightAt(28f, 32f), Is.EqualTo(9.5f).Within(HeightTolerance), "the west edge");
            Assert.That(HeightAt(36f, 32f), Is.EqualTo(9.5f).Within(HeightTolerance), "the east edge");
        }

        [Test]
        public void MovingOrRemovingTheWaterGivesTheGroundBack()
        {
            var lake = WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));
            Assert.That(HeightAt(20f, 20f), Is.LessThan(Ground - 1f));

            lake.transform.position = new Vector3(44f, 9.6f, 44f);
            WorldBuilder.Rebuild(scene);
            Assert.That(HeightAt(20f, 20f), Is.EqualTo(Ground).Within(HeightTolerance), "where the lake was");
            Assert.That(SandAt(20f, 20f), Is.Zero, "no sand where the lake was");
            Assert.That(HeightAt(44f, 44f), Is.LessThan(Ground - 1f), "where the lake is");

            Object.DestroyImmediate(lake.gameObject);
            WorldBuilder.Rebuild(scene);
            AssertGroundEverywhere();
        }

        [Test]
        public void GroundChangedByHandAwayFromTheWaterStays()
        {
            WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));
            var data = terrain.terrainData;
            data.SetHeights(60, 60, new[,] { { 0.6f } });

            WorldBuilder.Rebuild(scene);

            Assert.That(data.GetHeight(60, 60), Is.EqualTo(12f).Within(HeightTolerance));
            Assert.That(WorldBuilder.Rebuild(scene), Is.False, "nothing changed since");
        }

        // Terrain data that is an asset keeps what the World Builder changed as a hidden part of
        // itself that builds leave out, so the state is saved with the terrain. The part reads all
        // of the state back from what Unity serializes, as when the asset is saved and loaded.
        // It stays when the ground is given back, so the asset keeps the same parts when water
        // comes back.
        [Test]
        public void ATerrainAssetKeepsItsStateInAHiddenPartThatReadsItBack()
        {
            AssetDatabase.CreateFolder("Assets", AssetDirectory.Substring("Assets/".Length));
            AssetDatabase.CreateAsset(grass, AssetDirectory + "/Grass.terrainlayer");
            AssetDatabase.CreateAsset(sand, AssetDirectory + "/Sand.terrainlayer");
            var data = terrain.terrainData;
            AssetDatabase.CreateAsset(data, AssetDirectory + "/Terrain.asset");
            WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));
            var part = StateOf(data);
            Assert.That(part, Is.Not.Null, "the asset holds the state");
            Assert.That(part.hideFlags, Is.EqualTo(HideFlags.HideInHierarchy | HideFlags.DontSaveInBuild));

            // A copy of the part goes through Unity's serialization, as saving and loading do.
            var copy = Object.Instantiate(part);
            try
            {
                var kept = WorldBuilderTerrainState.Of(data);
                var read = copy.State;
                Assert.That(read, Is.Not.SameAs(kept));
                Assert.That(kept.Heights, Is.Not.Empty);
                Assert.That(read.HeightmapResolution, Is.EqualTo(kept.HeightmapResolution));
                Assert.That(read.Heights, Is.EquivalentTo(kept.Heights));
                Assert.That(
                    new[] { read.AlphamapResolution, read.BedLayerIndex },
                    Is.EqualTo(new[] { kept.AlphamapResolution, kept.BedLayerIndex }));
                Assert.That(read.LayerIds, Is.EqualTo(kept.LayerIds));
                Assert.That(read.Alphas.Keys, Is.EquivalentTo(kept.Alphas.Keys));
                foreach (var cell in kept.Alphas)
                {
                    Assert.That(read.Alphas[cell.Key].Bed, Is.EqualTo(cell.Value.Bed));
                    Assert.That(read.Alphas[cell.Key].Ground, Is.EqualTo(cell.Value.Ground));
                }
            }
            finally
            {
                Object.DestroyImmediate(copy);
            }

            TerrainCarver.Restore(terrain);
            Assert.That(HeightAt(20f, 20f), Is.EqualTo(Ground).Within(HeightTolerance), "the ground given back");
            Assert.That(WorldBuilderTerrainState.Of(data).IsEmpty, Is.True, "nothing left to take back");
            Assert.That(StateOf(data), Is.Not.Null, "the hidden part stays");
        }

        // The water shapes a terrain only while it carries a WorldBuilderTerrain, so an undo of
        // adding one gives the ground back.
        [Test]
        public void UndoingTheWaterShapingATerrainGivesTheGroundBack()
        {
            Object.DestroyImmediate(terrain.GetComponent<WorldBuilderTerrain>());
            WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));
            Assert.That(HeightAt(20f, 20f), Is.EqualTo(Ground).Within(HeightTolerance), "without the component the water leaves the terrain as it is");

            Undo.IncrementCurrentGroup();
            Undo.AddComponent<WorldBuilderTerrain>(terrain.gameObject);
            WorldBuilder.Rebuild(scene);
            Assert.That(HeightAt(20f, 20f), Is.LessThan(Ground - 1f));

            Undo.PerformUndo();
            WorldBuilder.Rebuild(scene);

            Assert.That(terrain.GetComponent<WorldBuilderTerrain>(), Is.Null);
            AssertGroundEverywhere();
        }

        // Two terrains that share their terrain data would each carve it at their own place, so the
        // water shapes neither, and the validation says why.
        [Test]
        public void TerrainsThatShareTheirTerrainDataStayAsTheyAreAndAreReported()
        {
            var copy = Terrain.CreateTerrainGameObject(terrain.terrainData);
            copy.transform.SetParent(terrain.transform.parent, false);
            copy.transform.localPosition = new Vector3(64f, 0f, 0f);
            var copyShaped = copy.AddComponent<WorldBuilderTerrain>();

            WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));

            AssertGroundEverywhere();
            var errors = WorldBuilder.Validate(scene).Where(issue => issue.Severity == WorldBuilderIssueSeverity.Error);
            Assert.That(
                errors.Select(issue => issue.Target),
                Is.EquivalentTo(new Object[] { terrain.GetComponent<WorldBuilderTerrain>(), copyShaped }));
        }

        // A river that starts in a lake lies at the lake's level where they meet, also a little
        // past the lake's water, and its bed follows that level there instead of the height of
        // its points, so no ground rises through the water.
        [Test]
        public void ARiverBedFollowsTheLevelOfTheLakeItStartsIn()
        {
            WorldBuilderCreate.AddLake(scene, new Vector3(32f, 9.6f, 32f));
            var river = WorldBuilderCreate.AddRiver(scene, Vector3.zero);
            var serialized = new SerializedObject(river);
            var points = serialized.FindProperty("points");
            points.arraySize = 2;
            foreach (var index in new[] { 0, 1 })
            {
                var element = points.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("Position").vector3Value = new Vector3(32f, 10f, index == 0 ? 34f : 60f);
                element.FindPropertyRelative("Width").floatValue = 4f;
                element.FindPropertyRelative("Depth").floatValue = 0.1f;
            }

            serialized.FindProperty("curved").boolValue = false;
            serialized.FindProperty("rowSpacing").floatValue = 1f;
            serialized.FindProperty("bankWidth").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            WorldBuilder.Rebuild(scene);

            // The river's points lie 0.4 m above the lake. The lake's water reaches 1.5 m past
            // its shore at Z 38, so the river's rows lie at the lake's level up to the row at Z
            // 40, past the lake's bank, where the channel lies 0.1 m below that level.
            Assert.That(HeightAt(32f, 40f), Is.EqualTo(9.6f - 0.1f).Within(HeightTolerance));
        }

        // The World Builder remembers what it painted by terrain layer, so removing a layer that
        // its paint does not use keeps that, and the sand still goes where the water moves away.
        [Test]
        public void RemovingAnUnusedLayerKeepsWhatTheWaterPainted()
        {
            var rock = new TerrainLayer { name = "Rock" };
            made.Add(rock);
            var data = terrain.terrainData;
            data.terrainLayers = new[] { grass, sand, rock };
            var lake = WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));
            Assert.That(SandAt(20f, 20f), Is.EqualTo(1f).Within(0.01f));

            data.terrainLayers = new[] { grass, sand };
            lake.transform.position = new Vector3(44f, 9.6f, 44f);
            WorldBuilder.Rebuild(scene);

            Assert.That(SandAt(20f, 20f), Is.Zero, "no sand where the lake was");
            Assert.That(SandAt(44f, 44f), Is.EqualTo(1f).Within(0.01f), "sand where the lake is");
        }

        // Removing a layer that lies under the paint too keeps what the World Builder painted, as
        // a share of what is left, so the sand still goes where the water moves away, also on the
        // bank, where the sand shares the cells with the ground.
        [Test]
        public void RemovingALayerUnderThePaintKeepsWhatTheWaterPainted()
        {
            var rock = new TerrainLayer { name = "Rock" };
            made.Add(rock);
            var data = terrain.terrainData;
            data.terrainLayers = new[] { grass, sand, rock };
            var maps = new float[128, 128, 3];
            for (var z = 0; z < 128; z++)
            {
                for (var x = 0; x < 128; x++)
                {
                    maps[z, x, 0] = 0.5f;
                    maps[z, x, 2] = 0.5f;
                }
            }

            data.SetAlphamaps(0, 0, maps);
            var lake = WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));
            Assert.That(SandAt(20f, 20f), Is.EqualTo(1f).Within(0.01f));

            data.terrainLayers = new[] { grass, sand };
            lake.transform.position = new Vector3(44f, 9.6f, 44f);
            WorldBuilder.Rebuild(scene);

            // The lake's floor and bank reached from 10 m to 30 m along X and from 12 m to 28 m
            // along Z, away from where the lake lies now.
            for (var z = 8f; z < 32f; z += 0.5f)
            {
                for (var x = 8f; x < 32f; x += 0.5f)
                {
                    Assert.That(SandAt(x, z), Is.LessThan(0.01f), "no sand at " + x + ", " + z);
                }
            }
        }

        // Giving the ground back changes no terrain data that another terrain uses too, such as a
        // terrain duplicated after the water shaped it.
        [Test]
        public void GivingTheGroundBackLeavesSharedTerrainDataAsItIs()
        {
            WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));
            var carved = HeightAt(20f, 20f);
            Assert.That(carved, Is.LessThan(Ground - 1f));
            var copy = Terrain.CreateTerrainGameObject(terrain.terrainData);
            copy.transform.SetParent(terrain.transform.parent, false);

            Assert.That(TerrainCarver.MayShape(terrain), Is.False);
            Assert.That(TerrainCarver.Restore(terrain), Is.False);
            Assert.That(HeightAt(20f, 20f), Is.EqualTo(carved).Within(HeightTolerance));
        }

        // A disabled lake keeps its water as it was, so it keeps its bed below that water too.
        [Test]
        public void ADisabledLakeKeepsItsBedBelowItsWater()
        {
            var lake = WorldBuilderCreate.AddLake(scene, new Vector3(32f, 9.6f, 32f));
            var carved = HeightAt(32f, 32f);
            Assert.That(carved, Is.LessThan(Ground - 1f));

            lake.enabled = false;
            WorldBuilder.Rebuild(scene);

            Assert.That(HeightAt(32f, 32f), Is.EqualTo(carved).Within(HeightTolerance), "the floor");
            Assert.That(SandAt(32f, 32f), Is.EqualTo(1f).Within(0.01f), "the sand");
        }

        // Two terrains side by side both reach a bed that ends at the edge they share, so their
        // edges stay level with each other at the water.
        [Test]
        public void TerrainsSideBySideBothReachABedThatEndsAtTheirSharedEdge()
        {
            var east = CreateTerrain(new Vector3(64f, 0f, 0f));

            // The default shore is 16 by 12 m, so it runs from X 64 to X 80.
            var lake = WorldBuilderCreate.AddLake(scene, new Vector3(72f, 9.6f, 32f));
            var serialized = new SerializedObject(lake);
            serialized.FindProperty("bankWidth").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            WorldBuilder.Rebuild(scene);

            Assert.That(terrain.terrainData.GetHeight(64, 32), Is.EqualTo(9.6f).Within(HeightTolerance), "the west terrain's last column");
            Assert.That(east.terrainData.GetHeight(0, 32), Is.EqualTo(9.6f).Within(HeightTolerance), "the east terrain's first column");
        }

        // Terrain data that a closed scene uses too, also through a prefab, which only the scene's
        // file tells, stays as it is, and the validation says why.
        [Test]
        public void TerrainDataThatAnotherSceneUsesThroughAPrefabStaysAsItIsAndIsReported()
        {
            AssetDatabase.CreateFolder("Assets", AssetDirectory.Substring("Assets/".Length));
            AssetDatabase.CreateAsset(grass, AssetDirectory + "/Grass.terrainlayer");
            AssetDatabase.CreateAsset(sand, AssetDirectory + "/Sand.terrainlayer");
            var data = terrain.terrainData;
            AssetDatabase.CreateAsset(data, AssetDirectory + "/Terrain.asset");
            var prefabTerrain = Terrain.CreateTerrainGameObject(data);
            var prefab = PrefabUtility.SaveAsPrefabAsset(prefabTerrain, AssetDirectory + "/Terrain.prefab");
            Object.DestroyImmediate(prefabTerrain);

            var otherPath = AssetDirectory + "/Other.unity";
            Assert.That(AssetDatabase.CopyAsset("Assets/Scenes/CharacterSelect.unity", otherPath), Is.True);
            var other = EditorSceneManager.OpenScene(otherPath, OpenSceneMode.Additive);
            PrefabUtility.InstantiatePrefab(prefab, other);
            EditorSceneManager.SaveScene(other);
            EditorSceneManager.CloseScene(other, true);

            WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));

            AssertGroundEverywhere();
            Assert.That(
                WorldBuilder.Validate(scene).Where(issue => issue.Severity == WorldBuilderIssueSeverity.Error).Select(issue => issue.Target),
                Is.EqualTo(new Object[] { terrain.GetComponent<WorldBuilderTerrain>() }));
        }

        // A curve through a river's points can bulge far past them, and the channel follows the
        // curve there too.
        [Test]
        public void ACurvedRiverCarvesItsChannelWhereTheCurveBulgesPastItsPoints()
        {
            var river = WorldBuilderCreate.AddRiver(scene, Vector3.zero);
            var serialized = new SerializedObject(river);
            var points = serialized.FindProperty("points");
            var corners = new[] { new Vector3(12f, 9.5f, 10f), new Vector3(12f, 9.5f, 50f), new Vector3(52f, 9.5f, 50f), new Vector3(52f, 9.5f, 10f) };
            points.arraySize = corners.Length;
            for (var index = 0; index < corners.Length; index++)
            {
                var element = points.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("Position").vector3Value = corners[index];
                element.FindPropertyRelative("Width").floatValue = 1f;
                element.FindPropertyRelative("Depth").floatValue = 1f;
            }

            serialized.FindProperty("curved").boolValue = true;
            serialized.FindProperty("bankWidth").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            WorldBuilder.Rebuild(scene);

            // The curve's middle lies 5 m past the points, at (32, 55), where the river is 1 m deep.
            Assert.That(HeightAt(32f, 55f), Is.EqualTo(8.5f).Within(0.05f));
        }

        // Terrain data saved as an asset after the water shaped it takes what the water changed
        // along, so the ground still comes back where the water moves away.
        [Test]
        public void TerrainDataSavedAfterTheWaterShapedItStillGivesTheGroundBack()
        {
            var lake = WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));
            Assert.That(HeightAt(20f, 20f), Is.LessThan(Ground - 1f));
            AssetDatabase.CreateFolder("Assets", AssetDirectory.Substring("Assets/".Length));
            AssetDatabase.CreateAsset(grass, AssetDirectory + "/Grass.terrainlayer");
            AssetDatabase.CreateAsset(sand, AssetDirectory + "/Sand.terrainlayer");
            AssetDatabase.CreateAsset(terrain.terrainData, AssetDirectory + "/Terrain.asset");

            lake.transform.position = new Vector3(44f, 9.6f, 44f);
            WorldBuilder.Rebuild(scene);

            Assert.That(HeightAt(20f, 20f), Is.EqualTo(Ground).Within(HeightTolerance), "where the lake was");
            Assert.That(SandAt(20f, 20f), Is.Zero, "no sand where the lake was");
            Assert.That(HeightAt(44f, 44f), Is.LessThan(Ground - 1f), "where the lake is");
            Assert.That(StateOf(terrain.terrainData), Is.Not.Null, "the state is a hidden part of the asset now");
        }

        [Test]
        public void ARiverCarvesAChannelAsDeepAsTheRiverInItsMiddle()
        {
            var river = WorldBuilderCreate.AddRiver(scene, Vector3.zero);
            var serialized = new SerializedObject(river);
            var points = serialized.FindProperty("points");
            points.arraySize = 2;
            foreach (var index in new[] { 0, 1 })
            {
                var element = points.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("Position").vector3Value = new Vector3(32f, 9.5f, index == 0 ? 4f : 60f);
                element.FindPropertyRelative("Width").floatValue = 8f;
                element.FindPropertyRelative("Depth").floatValue = index == 0 ? 2f : 0.8f;
            }

            serialized.FindProperty("curved").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            WorldBuilder.Rebuild(scene);

            Assert.That(HeightAt(32f, 4f), Is.EqualTo(7.5f).Within(HeightTolerance), "2 m deep at the first point");
            Assert.That(HeightAt(32f, 60f), Is.EqualTo(8.7f).Within(HeightTolerance), "0.8 m deep at the last point");
            Assert.That(HeightAt(36f, 32f), Is.EqualTo(9.5f).Within(HeightTolerance), "the channel meets the surface at the water's edge");
            Assert.That(HeightAt(39f, 32f), Is.EqualTo(Ground).Within(HeightTolerance), "the bank reaches the ground 3 m further");
        }

        [Test]
        public void ASeaKeepsFallingTowardTheHorizon()
        {
            // The terrain is the World's ground, so its west edge is the World's edge there.
            var sea = WorldBuilderCreate.AddSea(scene, new Vector3(0f, 9.5f, 32f));
            var serialized = new SerializedObject(sea);
            var outline = serialized.FindProperty("outline");
            outline.arraySize = 4;
            outline.GetArrayElementAtIndex(0).vector2Value = new Vector2(0f, -20f);
            outline.GetArrayElementAtIndex(1).vector2Value = new Vector2(0f, 20f);
            outline.GetArrayElementAtIndex(2).vector2Value = new Vector2(20f, 20f);
            outline.GetArrayElementAtIndex(3).vector2Value = new Vector2(20f, -20f);
            serialized.FindProperty("horizon").intValue = (int)HorizonSides.West;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            WorldBuilder.Rebuild(scene);

            // 20 m from the east shore, at the World's west edge, the floor lies 3 m down; 2 m
            // inside the shore it lies 0.3 m down.
            Assert.That(HeightAt(0f, 32f), Is.EqualTo(9.5f - (0.15f * 20f)).Within(HeightTolerance));
            Assert.That(HeightAt(18f, 32f), Is.EqualTo(9.5f - 0.3f).Within(HeightTolerance));
        }

        // On its north side too, the sea's floor reaches the World's edge, the terrain's last row
        // of samples, at the depth it falls to there, instead of stopping short of it, also
        // without a beach, where the bed's bounds end at that row.
        [TestCase(10f)]
        [TestCase(0f)]
        public void ASeaFloorReachesTheWorldsEdgeOnItsNorthSide(float beachWidth)
        {
            var sea = WorldBuilderCreate.AddSea(scene, new Vector3(32f, 9.5f, 64f));
            var serialized = new SerializedObject(sea);
            var outline = serialized.FindProperty("outline");
            outline.arraySize = 4;
            outline.GetArrayElementAtIndex(0).vector2Value = new Vector2(-20f, -20f);
            outline.GetArrayElementAtIndex(1).vector2Value = new Vector2(-20f, 0f);
            outline.GetArrayElementAtIndex(2).vector2Value = new Vector2(20f, 0f);
            outline.GetArrayElementAtIndex(3).vector2Value = new Vector2(20f, -20f);
            serialized.FindProperty("horizon").intValue = (int)HorizonSides.North;
            serialized.FindProperty("bankWidth").floatValue = beachWidth;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            WorldBuilder.Rebuild(scene);

            // 20 m from the shore on every other side, at the edge and 1 m inside it.
            Assert.That(HeightAt(32f, 64f), Is.EqualTo(9.5f - (0.15f * 20f)).Within(HeightTolerance));
            Assert.That(HeightAt(32f, 63f), Is.EqualTo(9.5f - (0.15f * 19f)).Within(HeightTolerance));
        }

        [Test]
        public void AWaterfallDigsAPoolWhereItLands()
        {
            var lake = WorldBuilderCreate.AddLake(scene, new Vector3(32f, 9.6f, 32f));
            var river = WorldBuilderCreate.AddRiver(scene, Vector3.zero);
            var serialized = new SerializedObject(river);
            var points = serialized.FindProperty("points");
            foreach (var index in new[] { 0, 1 })
            {
                var element = points.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("Position").vector3Value = new Vector3(32f, 13f, index == 0 ? 0f : 26f);
            }

            serialized.FindProperty("curved").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var waterfall = WorldBuilderCreate.AddWaterfall(river);
            Assert.That(waterfall.Landing, Is.EqualTo(lake.Body));
            WorldBuilder.Rebuild(scene);

            // The water leaves the lip at 1.6 m/s and falls 3.4 m, so it lands 1.33 m out.
            var landing = 26f + (1.6f * Mathf.Sqrt(2f * 3.4f / 9.81f));
            var floor = 9.6f - (0.3f * (landing - 26f));
            Assert.That(HeightAt(32f, landing), Is.LessThan(floor - 0.5f), "the pool lies deeper than the floor around it");
        }

        [Test]
        public void ATerrainWithoutWaterIsLeftAsItIs()
        {
            Assert.That(WorldBuilder.Rebuild(scene), Is.False);
            AssertGroundEverywhere();
        }

        // A 64 m terrain at the position below the World with flat ground at 10 m and grass, that
        // the water shapes and paints with sand.
        private Terrain CreateTerrain(Vector3 position)
        {
            var data = new TerrainData
            {
                heightmapResolution = 65,
                alphamapResolution = 128
            };
            data.size = new Vector3(64f, 20f, 64f);
            made.Add(data);
            var heights = new float[65, 65];
            for (var z = 0; z < 65; z++)
            {
                for (var x = 0; x < 65; x++)
                {
                    heights[z, x] = Ground / 20f;
                }
            }

            data.SetHeights(0, 0, heights);
            data.terrainLayers = new[] { grass, sand };
            var maps = new float[128, 128, 2];
            for (var z = 0; z < 128; z++)
            {
                for (var x = 0; x < 128; x++)
                {
                    maps[z, x, 0] = 1f;
                }
            }

            data.SetAlphamaps(0, 0, maps);
            var terrainObject = Terrain.CreateTerrainGameObject(data);
            terrainObject.transform.SetParent(world.transform, false);
            terrainObject.transform.localPosition = position;
            var shaped = new SerializedObject(terrainObject.AddComponent<WorldBuilderTerrain>());
            shaped.FindProperty("bedLayer").objectReferenceValue = sand;
            shaped.ApplyModifiedPropertiesWithoutUndo();
            return terrainObject.GetComponent<Terrain>();
        }

        private static WorldBuilderTerrainStateAsset StateOf(TerrainData data)
        {
            return AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(data)).OfType<WorldBuilderTerrainStateAsset>().FirstOrDefault();
        }

        private float HeightAt(float x, float z)
        {
            return terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.GetPosition().y;
        }

        private float SandAt(float x, float z)
        {
            var data = terrain.terrainData;
            var cell = new Vector2Int(
                Mathf.Clamp(Mathf.FloorToInt(x / data.size.x * data.alphamapResolution), 0, data.alphamapResolution - 1),
                Mathf.Clamp(Mathf.FloorToInt(z / data.size.z * data.alphamapResolution), 0, data.alphamapResolution - 1));
            return data.GetAlphamaps(cell.x, cell.y, 1, 1)[0, 0, 1];
        }

        private void AssertGroundEverywhere()
        {
            var data = terrain.terrainData;
            var heights = data.GetHeights(0, 0, data.heightmapResolution, data.heightmapResolution);
            foreach (var height in heights)
            {
                Assert.That(height * data.size.y, Is.EqualTo(Ground).Within(HeightTolerance));
            }

            var maps = data.GetAlphamaps(0, 0, data.alphamapResolution, data.alphamapResolution);
            for (var z = 0; z < data.alphamapResolution; z++)
            {
                for (var x = 0; x < data.alphamapResolution; x++)
                {
                    Assert.That(maps[z, x, 1], Is.Zero, "no sand");
                }
            }
        }
    }
}
