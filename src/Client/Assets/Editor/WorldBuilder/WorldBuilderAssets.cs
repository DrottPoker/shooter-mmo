using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Editor
{
    // The mesh assets that hold what the World Builder makes. A shape keeps the mesh asset its
    // MeshFilter already holds while no other shape, no other object of its scene, and no other
    // scene uses it; a new shape, or one that shares its mesh, such as a duplicated lake, the
    // copy of a disabled lake, a shape copied from another scene, or a scene saved under a new
    // name, gets a mesh of its own below Assets/Art/Worlds/<scene>. A scene that was never saved
    // keeps its meshes in itself, until it is saved and rebuilt.
    internal sealed class WorldBuilderAssets
    {
        public const string Root = "Assets/Art/Worlds";

        private static readonly Dictionary<string, string[]> SceneDependencies = new Dictionary<string, string[]>();
        private static List<string> scenePaths;

        private readonly HashSet<Mesh> claimed = new HashSet<Mesh>();
        private readonly Dictionary<Scene, HashSet<string>> usedElsewhere = new Dictionary<Scene, HashSet<string>>();
        private readonly Dictionary<Scene, HashSet<Mesh>> heldInScene = new Dictionary<Scene, HashSet<Mesh>>();

        // The mesh this build writes for the filter, which no other shape of the build writes.
        public Mesh MeshOf(MeshFilter filter, string name)
        {
            var mesh = filter.sharedMesh;
            var scene = filter.gameObject.scene;
            if (mesh != null && MayWrite(mesh, scene) && claimed.Add(mesh))
            {
                return mesh;
            }

            mesh = IsSaved(scene) ? Create(DefaultDirectory(scene), name) : new Mesh { name = name };
            filter.sharedMesh = mesh;
            EditorUtility.SetDirty(filter);
            claimed.Add(mesh);
            return mesh;
        }

        // Whether a shape of this build writes the mesh.
        public bool IsClaimed(Mesh mesh)
        {
            return claimed.Contains(mesh);
        }

        // The mesh of a part next to another mesh, such as the sea beyond the World next to the
        // sea's surface, named after it.
        public Mesh MeshNextTo(MeshFilter filter, Mesh owner, string suffix)
        {
            var mesh = filter.sharedMesh;
            var scene = filter.gameObject.scene;
            if (mesh != null && MayWrite(mesh, scene) && claimed.Add(mesh))
            {
                return mesh;
            }

            if (!AssetDatabase.Contains(owner))
            {
                mesh = new Mesh { name = owner.name + suffix };
                filter.sharedMesh = mesh;
                EditorUtility.SetDirty(filter);
                claimed.Add(mesh);
                return mesh;
            }

            var directory = Path.GetDirectoryName(AssetDatabase.GetAssetPath(owner)).Replace('\\', '/');
            mesh = AssetDatabase.LoadAssetAtPath<Mesh>(directory + "/" + owner.name + suffix + ".asset");
            if (mesh == null || !MayWrite(mesh, scene) || !claimed.Add(mesh))
            {
                mesh = Create(directory, owner.name + suffix);
                claimed.Add(mesh);
            }

            filter.sharedMesh = mesh;
            EditorUtility.SetDirty(filter);
            return mesh;
        }

        public static string DefaultDirectory(Scene scene)
        {
            return Root + "/" + scene.name;
        }

        // Whether a build of a scene may write into a mesh: in a saved scene, a mesh asset of its
        // own that no other scene uses, and in a scene that was never saved, a mesh that is no
        // asset. A build leaves every other mesh as it is and gives the shape a mesh of its own.
        public static bool MayWrite(Mesh mesh, bool sceneSaved, ICollection<string> usedElsewhere)
        {
            if (!AssetDatabase.Contains(mesh))
            {
                return !sceneSaved;
            }

            return sceneSaved && IsOwnAsset(mesh) && !usedElsewhere.Contains(AssetDatabase.GetAssetPath(mesh));
        }

        // Whether this build may write into a mesh of a shape in the scene: never into one that an
        // object of the scene holds which the build does not write.
        public bool MayWrite(Mesh mesh, Scene scene)
        {
            if (HeldInScene(scene).Contains(mesh))
            {
                return false;
            }

            var saved = IsSaved(scene);
            return MayWrite(mesh, saved, saved && AssetDatabase.Contains(mesh) ? UsedElsewhere(scene) : new HashSet<string>());
        }

        // Whether the build writes a filter's mesh: a filter of an enabled shape on an active
        // object, or the sea's water beyond the World or its seabed below such a sea.
        public static bool IsBuilt(MeshFilter filter)
        {
            var shape = filter.GetComponent<WorldBuilderShape>();
            var parent = filter.transform.parent;
            if (shape == null && parent != null && (filter.name == WaterBuilder.HorizonName || filter.name == WaterBuilder.SeabedName))
            {
                shape = parent.GetComponent<SeaShape>();
            }

            return shape != null && shape.enabled && shape.gameObject.activeInHierarchy;
        }

        // The meshes that objects of the scene hold which the build does not write, such as a
        // disabled lake's, so the build leaves them as they are.
        private HashSet<Mesh> HeldInScene(Scene scene)
        {
            if (heldInScene.TryGetValue(scene, out var held))
            {
                return held;
            }

            held = new HashSet<Mesh>(scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<MeshFilter>(true))
                .Where(filter => filter.sharedMesh != null && !IsBuilt(filter))
                .Select(filter => filter.sharedMesh));
            heldInScene.Add(scene, held);
            return held;
        }

        // The assets that the scenes other than this one use: those saved, as their files say, and
        // those open, as they hold them now.
        private HashSet<string> UsedElsewhere(Scene scene)
        {
            if (usedElsewhere.TryGetValue(scene, out var used))
            {
                return used;
            }

            used = DependenciesOfOtherScenes(scene.path);
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var other = SceneManager.GetSceneAt(index);
                if (other == scene || !other.isLoaded)
                {
                    continue;
                }

                foreach (var filter in other.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MeshFilter>(true)))
                {
                    if (filter.sharedMesh != null && AssetDatabase.Contains(filter.sharedMesh))
                    {
                        used.Add(AssetDatabase.GetAssetPath(filter.sharedMesh));
                    }
                }
            }

            usedElsewhere.Add(scene, used);
            return used;
        }

        // What the saved scenes other than the one at the path use, as their files say. The files
        // change rarely, so they are read once until the project changes, which keeps a rebuild
        // fast while a shape is dragged.
        private static HashSet<string> DependenciesOfOtherScenes(string scenePath)
        {
            if (scenePaths == null)
            {
                scenePaths = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).ToList();
            }

            var used = new HashSet<string>();
            foreach (var path in scenePaths)
            {
                if (path == scenePath)
                {
                    continue;
                }

                if (!SceneDependencies.TryGetValue(path, out var dependencies))
                {
                    dependencies = AssetDatabase.GetDependencies(path, false);
                    SceneDependencies.Add(path, dependencies);
                }

                used.UnionWith(dependencies);
            }

            return used;
        }

        [InitializeOnLoadMethod]
        private static void ForgetScenesWhenTheProjectChanges()
        {
            EditorApplication.projectChanged += ForgetScenes;
            EditorSceneManager.sceneSaved += scene => ForgetScenes();
        }

        private static void ForgetScenes()
        {
            scenePaths = null;
            SceneDependencies.Clear();
        }

        // The meshes below the scene's own folder that neither the scene nor another scene uses any
        // more, such as the mesh of a lake that was removed again. What the open scenes hold
        // counts as they hold it now, such as a lake moved into another scene not saved yet.
        public static List<Mesh> UnusedMeshes(Scene scene)
        {
            if (!IsSaved(scene))
            {
                return new List<Mesh>();
            }

            var open = new List<Scene>();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                open.Add(SceneManager.GetSceneAt(index));
            }

            return UnusedMeshes(DefaultDirectory(scene), MeshesOf(open), scene.path);
        }

        // The meshes that the loaded scenes' mesh filters hold now.
        public static HashSet<Mesh> MeshesOf(IEnumerable<Scene> scenes)
        {
            return new HashSet<Mesh>(scenes
                .Where(scene => scene.IsValid() && scene.isLoaded)
                .SelectMany(scene => scene.GetRootGameObjects())
                .SelectMany(root => root.GetComponentsInChildren<MeshFilter>(true))
                .Select(filter => filter.sharedMesh)
                .Where(mesh => mesh != null));
        }

        // The meshes below the directory that are not among the used ones and that no scene other
        // than the one at the given path uses.
        public static List<Mesh> UnusedMeshes(string directory, ICollection<Mesh> used, string scenePath)
        {
            var unused = new List<Mesh>();
            if (!AssetDatabase.IsValidFolder(directory))
            {
                return unused;
            }

            foreach (var guid in AssetDatabase.FindAssets("t:Mesh", new[] { directory }))
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid));
                if (mesh != null && !used.Contains(mesh))
                {
                    unused.Add(mesh);
                }
            }

            if (unused.Count == 0)
            {
                return unused;
            }

            // The scene's own file may be out of date, so only the other scenes are read from disk.
            var elsewhere = DependenciesOfOtherScenes(scenePath);
            unused.RemoveAll(mesh => elsewhere.Contains(AssetDatabase.GetAssetPath(mesh)));
            return unused;
        }

        // Moves the scene's unused meshes to the trash, from where they can be restored. Returns
        // how many it moved.
        public static int RemoveUnusedMeshes(Scene scene)
        {
            var paths = UnusedMeshes(scene).Select(AssetDatabase.GetAssetPath).ToArray();
            if (paths.Length == 0)
            {
                return 0;
            }

            var failed = new List<string>();
            AssetDatabase.MoveAssetsToTrash(paths, failed);
            foreach (var path in failed)
            {
                Debug.LogWarning("[WORLD BUILDER] Could not move the unused mesh " + path + " to the trash.");
            }

            return paths.Length - failed.Count;
        }

        private static bool IsSaved(Scene scene)
        {
            return !string.IsNullOrEmpty(scene.path);
        }

        // A mesh asset of its own, not part of an imported model or another asset.
        private static bool IsOwnAsset(Mesh mesh)
        {
            var path = AssetDatabase.GetAssetPath(mesh);
            return path.EndsWith(".asset", System.StringComparison.Ordinal)
                && AssetDatabase.IsMainAsset(mesh);
        }

        private static Mesh Create(string directory, string name)
        {
            EnsureFolder(directory);
            var path = AssetDatabase.GenerateUniqueAssetPath(directory + "/" + name + ".asset");
            var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static void EnsureFolder(string directory)
        {
            if (AssetDatabase.IsValidFolder(directory))
            {
                return;
            }

            var parent = Path.GetDirectoryName(directory).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(directory));
        }
    }
}
