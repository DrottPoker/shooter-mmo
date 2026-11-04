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

        private Scene scene;
        private Terrain terrain;
        private TerrainLayer grass;
        private TerrainLayer sand;

        [SetUp]
        public void CreateTerrain()
        {
            scene = EditorSceneManager.NewPreviewScene();
            grass = new TerrainLayer { name = "Grass" };
            sand = new TerrainLayer { name = "Sand" };
            var data = new TerrainData
            {
                heightmapResolution = 65,
                alphamapResolution = 128
            };
            data.size = new Vector3(64f, 20f, 64f);
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
            var world = new GameObject("World");
            SceneManager.MoveGameObjectToScene(world, scene);
            world.AddComponent<WorldCollisionAuthoring>();
            var terrainObject = Terrain.CreateTerrainGameObject(data);
            terrainObject.transform.SetParent(world.transform, false);
            terrain = terrainObject.GetComponent<Terrain>();
            var shaped = new SerializedObject(terrainObject.AddComponent<WorldBuilderTerrain>());
            shaped.FindProperty("bedLayer").objectReferenceValue = sand;
            shaped.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void CloseScene()
        {
            var data = terrain.terrainData;
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
            foreach (var made in new Object[] { data, grass, sand })
            {
                if (made != null && !AssetDatabase.Contains(made))
                {
                    Object.DestroyImmediate(made);
                }
            }
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
                    new[] { read.AlphamapResolution, read.AlphamapLayers, read.BedLayerIndex },
                    Is.EqualTo(new[] { kept.AlphamapResolution, kept.AlphamapLayers, kept.BedLayerIndex }));
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
        // of samples, at the depth it falls to there, instead of stopping short of it.
        [Test]
        public void ASeaFloorReachesTheWorldsEdgeOnItsNorthSide()
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
