using Mmorpg.Client.Editor;
using Mmorpg.Client.Worlds;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Tests.EditMode
{
    // Throwing away a scene's unsaved changes, as Don't Save does, also throws away what the World
    // Builder made from them: the water meshes and the terrain are assets, which the scene does
    // not take back itself, so the World Builder rebuilds them from the saved scene. The test
    // saves a scene of its own with a terrain and a lake below a folder that it removes again.
    public sealed class WorldBuilderDiscardTests
    {
        private const string Directory = "Assets/WorldBuilderDiscardTestAssets";
        private const string SceneName = "WorldBuilderDiscardTest";
        private const float Ground = 10f;

        private bool worldsFolderExisted;

        [SetUp]
        public void CreateFolder()
        {
            worldsFolderExisted = AssetDatabase.IsValidFolder(WorldBuilderAssets.Root);
            AssetDatabase.CreateFolder("Assets", Directory.Substring("Assets/".Length));
        }

        [TearDown]
        public void RemoveAssets()
        {
            var scene = SceneManager.GetSceneByPath(ScenePath);
            if (scene.IsValid() && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }

            AssetDatabase.DeleteAsset(Directory);
            AssetDatabase.DeleteAsset(WorldBuilderAssets.Root + "/" + SceneName);
            if (!worldsFolderExisted)
            {
                AssetDatabase.DeleteAsset(WorldBuilderAssets.Root);
            }
        }

        private static string ScenePath
        {
            get { return Directory + "/" + SceneName + ".unity"; }
        }

        [Test]
        public void ThrowingAwayASceneRebuildsItsWaterAndBedsFromTheSavedScene()
        {
            Assert.That(AssetDatabase.CopyAsset("Assets/Scenes/CharacterSelect.unity", ScenePath), Is.True);
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            var data = CreateTerrain(scene);
            var lake = WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));
            EditorSceneManager.SaveScene(scene);
            var carved = data.GetHeight(20, 20);
            Assert.That(carved, Is.LessThan(Ground - 1f));

            lake.transform.position = new Vector3(44f, 9.6f, 44f);
            WorldBuilder.Rebuild(scene);
            Assert.That(data.GetHeight(20, 20), Is.EqualTo(Ground).Within(0.002f), "the moved lake gave the ground back");
            EditorSceneManager.CloseScene(scene, true);
            Assert.That(WorldBuilderDiscards.Pending, Does.Contain(ScenePath));

            // A moment later the World Builder rebuilds the scene from its file.
            WorldBuilderDiscards.RepairPending();

            Assert.That(WorldBuilderDiscards.Pending, Does.Not.Contain(ScenePath));
            Assert.That(SceneManager.GetSceneByPath(ScenePath).isLoaded, Is.False, "the scene is closed again");
            Assert.That(data.GetHeight(20, 20), Is.EqualTo(carved).Within(0.002f), "the bed is back where the saved lake lies");
            Assert.That(data.GetHeight(44, 44), Is.EqualTo(Ground).Within(0.002f), "the ground is back where the thrown-away lake lay");
        }

        // A 64 m terrain asset with flat ground at 10 m, below the scene's World, that the water
        // shapes.
        private static TerrainData CreateTerrain(Scene scene)
        {
            var data = new TerrainData { heightmapResolution = 65 };
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
            AssetDatabase.CreateAsset(data, Directory + "/Terrain.asset");
            var world = new GameObject("World");
            SceneManager.MoveGameObjectToScene(world, scene);
            world.AddComponent<WorldCollisionAuthoring>();
            var terrainObject = Terrain.CreateTerrainGameObject(data);
            terrainObject.transform.SetParent(world.transform, false);
            terrainObject.AddComponent<WorldBuilderTerrain>();
            return data;
        }
    }
}
