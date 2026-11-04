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
    // them they would still show what was thrown away. Each saved scene whose water or beds a
    // rebuild changed is noted until it is saved, in the Editor's preferences, which a compile,
    // Play Mode, and a restart keep. A noted scene that is no longer open with unsaved changes is
    // rebuilt from its saved file a moment after it closes, after Play Mode, and when the Editor
    // starts. A terrain's changes made with Unity's terrain tools stay, as they do in every terrain.
    [InitializeOnLoad]
    internal static class WorldBuilderDiscards
    {
        // Whether a noted scene is being rebuilt from its saved file.
        private static bool repairing;

        static WorldBuilderDiscards()
        {
            WorldBuilder.Rebuilt += OnRebuilt;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += scene => Forget(scene.path);
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredEditMode)
                {
                    ScheduleRepair();
                }
            };

            // After a compile, and when the Editor starts after it quit or stopped with a noted scene.
            ScheduleRepair();
        }

        // The saved scenes whose water or beds a rebuild changed since they were last saved.
        public static IReadOnlyList<string> Changed
        {
            get { return Load(); }
        }

        // Where the notes are kept, for each project.
        internal static string PreferenceKey
        {
            get { return "Mmorpg.WorldBuilder.ChangedScenes." + Application.dataPath; }
        }

        // Rebuilds every noted scene that is not open with unsaved changes from its saved file,
        // unless the Editor plays: a scene open again as it was saved is rebuilt as it is, and any
        // other is opened beside the open ones for its rebuild and closed again without being saved.
        public static void Repair()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            foreach (var path in Load())
            {
                var scene = SceneManager.GetSceneByPath(path);
                if (scene.isLoaded && scene.isDirty)
                {
                    continue;
                }

                Forget(path);
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                {
                    continue;
                }

                // A scene listed unloaded in the Hierarchy stays listed there.
                var listed = scene.IsValid();
                var opened = !scene.isLoaded;
                repairing = true;
                try
                {
                    if (opened)
                    {
                        scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    }

                    if (WorldBuilder.Rebuild(scene))
                    {
                        Debug.Log("[WORLD BUILDER] Rebuilt the water and beds of " + path + " from the saved scene, since its unsaved changes were thrown away.");
                    }
                }
                finally
                {
                    if (opened && scene.IsValid() && scene.isLoaded)
                    {
                        EditorSceneManager.CloseScene(scene, !listed);
                    }

                    repairing = false;
                }
            }
        }

        private static void ScheduleRepair()
        {
            EditorApplication.delayCall -= Repair;
            EditorApplication.delayCall += Repair;
        }

        private static void OnRebuilt(Scene scene, bool changed)
        {
            if (changed && !repairing && !string.IsNullOrEmpty(scene.path) && !EditorApplication.isPlaying)
            {
                var notes = Load();
                if (!notes.Contains(scene.path))
                {
                    notes.Add(scene.path);
                    Save(notes);
                }
            }
        }

        // A scene saved under another name leaves its old file as it was, and what the World
        // Builder made for it belongs to the scene under the new name from then on.
        private static void OnSceneSaving(Scene scene, string path)
        {
            if (scene.path != path)
            {
                Forget(scene.path);
            }
        }

        // A noted scene that closes throws its unsaved changes away.
        private static void OnSceneClosing(Scene scene, bool removingScene)
        {
            if (!repairing && Load().Contains(scene.path))
            {
                ScheduleRepair();
            }
        }

        private static List<string> Load()
        {
            return EditorPrefs.GetString(PreferenceKey, string.Empty)
                .Split('\n')
                .Where(path => path.Length > 0)
                .Distinct()
                .ToList();
        }

        private static void Save(List<string> notes)
        {
            if (notes.Count == 0)
            {
                EditorPrefs.DeleteKey(PreferenceKey);
            }
            else
            {
                EditorPrefs.SetString(PreferenceKey, string.Join("\n", notes));
            }
        }

        private static void Forget(string path)
        {
            var notes = Load();
            if (notes.Remove(path))
            {
                Save(notes);
            }
        }
    }
}
