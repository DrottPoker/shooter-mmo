using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Editor
{
    // Keeps what the World Builder makes for a scene in step with the scene when the scene's
    // unsaved changes are thrown away, such as with Don't Save. The water meshes and the terrains
    // are assets, which a scene does not take back with its own changes, so after a rebuild changed
    // them they would still show what was thrown away. A moment after such a scene closes without
    // being saved, or the next time the Editor starts when it quit then, the World Builder rebuilds
    // the scene's water and beds from the scene as it was saved. A terrain's changes made with
    // Unity's terrain tools stay, as they do in every terrain.
    [InitializeOnLoad]
    internal static class WorldBuilderDiscards
    {
        // The saved scenes whose water or beds a rebuild changed since they were last saved.
        private static readonly HashSet<string> Changed = new HashSet<string>();

        // Whether a scene is being rebuilt from its saved file, which opens and closes it.
        private static bool repairing;

        static WorldBuilderDiscards()
        {
            WorldBuilder.Rebuilt += OnRebuilt;
            EditorSceneManager.sceneSaved += scene => Changed.Remove(scene.path);
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredEditMode)
                {
                    EditorApplication.delayCall += RepairPending;
                }
            };
            EditorApplication.delayCall += RepairPending;
        }

        // The scenes waiting to be rebuilt from their saved files, kept across Editor sessions.
        public static IReadOnlyList<string> Pending
        {
            get { return LoadPending(); }
        }

        private static string PendingKey
        {
            get { return "Mmorpg.WorldBuilder.PendingRepairs." + Application.dataPath; }
        }

        // Rebuilds every scene waiting for it from its saved file, unless the Editor plays: a scene
        // that is open again is rebuilt as it is, and any other is opened beside the open ones
        // for its rebuild and closed again without being saved.
        public static void RepairPending()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            foreach (var path in LoadPending())
            {
                RemovePending(path);
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                {
                    continue;
                }

                var scene = SceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded)
                {
                    WorldBuilder.Rebuild(scene);
                    continue;
                }

                repairing = true;
                try
                {
                    scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    if (WorldBuilder.Rebuild(scene))
                    {
                        Debug.Log("[WORLD BUILDER] Rebuilt the water and beds of " + path + " from the saved scene, since its unsaved changes were thrown away.");
                    }
                }
                finally
                {
                    if (scene.IsValid() && scene.isLoaded)
                    {
                        EditorSceneManager.CloseScene(scene, true);
                    }

                    repairing = false;
                }
            }
        }

        private static void OnRebuilt(Scene scene, bool changed)
        {
            if (changed && !repairing && !string.IsNullOrEmpty(scene.path) && !EditorApplication.isPlaying)
            {
                Changed.Add(scene.path);
            }
        }

        // A scene that closes with unsaved changes throws them away.
        private static void OnSceneClosing(Scene scene, bool removingScene)
        {
            if (!repairing && scene.isDirty && Changed.Remove(scene.path))
            {
                AddPending(scene.path);
                EditorApplication.delayCall += RepairPending;
            }
        }

        // Scenes left unsaved when the Editor quits are rebuilt the next time it starts.
        private static void OnQuitting()
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.isDirty && Changed.Contains(scene.path))
                {
                    AddPending(scene.path);
                }
            }
        }

        private static List<string> LoadPending()
        {
            return EditorPrefs.GetString(PendingKey, string.Empty)
                .Split('\n')
                .Where(path => path.Length > 0)
                .Distinct()
                .ToList();
        }

        private static void SavePending(List<string> pending)
        {
            if (pending.Count == 0)
            {
                EditorPrefs.DeleteKey(PendingKey);
            }
            else
            {
                EditorPrefs.SetString(PendingKey, string.Join("\n", pending));
            }
        }

        private static void AddPending(string path)
        {
            var pending = LoadPending();
            if (!pending.Contains(path))
            {
                pending.Add(path);
                SavePending(pending);
            }
        }

        private static void RemovePending(string path)
        {
            var pending = LoadPending();
            if (pending.Remove(path))
            {
                SavePending(pending);
            }
        }
    }
}
