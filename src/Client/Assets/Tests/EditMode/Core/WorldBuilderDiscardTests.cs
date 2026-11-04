using System.Collections.Generic;
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
    // not take back itself, so the World Builder rebuilds them from the saved scene. Each test
    // saves a scene of its own with a terrain and a lake below a folder that it removes again.
    public sealed class WorldBuilderDiscardTests
    {
        private const string Directory = "Assets/WorldBuilderDiscardTestAssets";
        private const string SceneName = "WorldBuilderDiscardTest";
        private const float Ground = 10f;

        private bool worldsFolderExisted;
        private Scene scene;
        private TerrainData data;
        private LakeShape lake;
        private float carved;

        [SetUp]
        public void CreateFolder()
        {
            worldsFolderExisted = AssetDatabase.IsValidFolder(WorldBuilderAssets.Root);
            AssetDatabase.CreateFolder("Assets", Directory.Substring("Assets/".Length));
        }

        [TearDown]
        public void RemoveAssets()
        {
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
            OpenSavedScene();
            MoveTheLake();
            EditorSceneManager.CloseScene(scene, true);
            Assert.That(WorldBuilderDiscards.Changed, Does.Contain(ScenePath));

            // A moment later the World Builder rebuilds the scene from its file.
            WorldBuilderDiscards.Repair();

            Assert.That(WorldBuilderDiscards.Changed, Does.Not.Contain(ScenePath));
            Assert.That(SceneManager.GetSceneByPath(ScenePath).isLoaded, Is.False, "the scene is closed again");
            AssertAsSaved();
        }

        // A compile or Play Mode, which reload the scripts, also run the repair, and the note
        // lives in the Editor's preferences, which they and a restart keep.
        [Test]
        public void ASceneStillOpenWithItsChangesKeepsThemAndItsNote()
        {
            OpenSavedScene();
            MoveTheLake();
            Assert.That(EditorPrefs.GetString(WorldBuilderDiscards.PreferenceKey, string.Empty).Split('\n'), Does.Contain(ScenePath));

            WorldBuilderDiscards.Repair();

            Assert.That(WorldBuilderDiscards.Changed, Does.Contain(ScenePath));
            Assert.That(data.GetHeight(20, 20), Is.EqualTo(Ground).Within(0.002f), "the open scene keeps its moved lake");
            EditorSceneManager.CloseScene(scene, true);
            WorldBuilderDiscards.Repair();
            Assert.That(WorldBuilderDiscards.Changed, Does.Not.Contain(ScenePath));
            AssertAsSaved();
        }

        // Such as after Don't Save when the scene itself is opened again, or after a restart.
        [Test]
        public void ASceneOpenAgainAsItWasSavedIsRebuiltAsItIs()
        {
            OpenSavedScene();
            MoveTheLake();
            EditorSceneManager.CloseScene(scene, true);
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);

            WorldBuilderDiscards.Repair();

            Assert.That(WorldBuilderDiscards.Changed, Does.Not.Contain(ScenePath));
            Assert.That(scene.isLoaded, Is.True, "the scene stays open");
            AssertAsSaved();
        }

        // What the World Builder made belongs to the scene under its new name from then on, so the
        // old file is not rebuilt.
        [Test]
        public void ASceneSavedUnderAnotherNameKeepsWhatItMade()
        {
            OpenSavedScene();
            MoveTheLake();
            var copy = Directory + "/" + SceneName + "Copy.unity";
            Assert.That(EditorSceneManager.SaveScene(scene, copy), Is.True);

            var rebuilt = new List<string>();
            System.Action<Scene, bool> onRebuilt = (built, changed) => rebuilt.Add(built.path);
            WorldBuilder.Rebuilt += onRebuilt;
            try
            {
                WorldBuilderDiscards.Repair();
            }
            finally
            {
                WorldBuilder.Rebuilt -= onRebuilt;
            }

            Assert.That(rebuilt, Does.Not.Contain(ScenePath), "the old file is not rebuilt");
            Assert.That(WorldBuilderDiscards.Changed, Does.Not.Contain(ScenePath).And.Not.Contain(copy));
            Assert.That(data.GetHeight(20, 20), Is.EqualTo(Ground).Within(0.002f), "the beds stay where the moved lake lies");
        }

        // A saved scene with a terrain and a lake that carved its bed.
        private void OpenSavedScene()
        {
            Assert.That(AssetDatabase.CopyAsset("Assets/Scenes/CharacterSelect.unity", ScenePath), Is.True);
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            data = CreateTerrain(scene);
            lake = WorldBuilderCreate.AddLake(scene, new Vector3(20f, 9.6f, 20f));
            EditorSceneManager.SaveScene(scene);
            carved = data.GetHeight(20, 20);
            Assert.That(carved, Is.LessThan(Ground - 1f));
            Assert.That(WorldBuilderDiscards.Changed, Does.Not.Contain(ScenePath), "saving forgets the scene's note");
        }

        private void MoveTheLake()
        {
            lake.transform.position = new Vector3(44f, 9.6f, 44f);
            WorldBuilder.Rebuild(scene);
            Assert.That(data.GetHeight(20, 20), Is.EqualTo(Ground).Within(0.002f), "the moved lake gave the ground back");
            Assert.That(WorldBuilderDiscards.Changed, Does.Contain(ScenePath));
        }

        private void AssertAsSaved()
        {
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
