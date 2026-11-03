using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Editor
{
    // The mesh assets that hold what the World Builder makes. A shape keeps the mesh asset its
    // MeshFilter already holds; a new shape, or one that shares its mesh with another, such as a
    // duplicated lake, gets a mesh of its own below Assets/Art/Worlds/<scene>. A scene that was
    // never saved keeps its meshes in itself, until it is saved and rebuilt.
    internal sealed class WorldBuilderAssets
    {
        public const string Root = "Assets/Art/Worlds";

        private readonly HashSet<Mesh> claimed = new HashSet<Mesh>();

        // The mesh this build writes for the filter, which no other shape of the build writes.
        public Mesh MeshOf(MeshFilter filter, string name)
        {
            var mesh = filter.sharedMesh;
            var scene = filter.gameObject.scene;
            if (mesh != null && (IsOwnAsset(mesh) || (!IsSaved(scene) && !AssetDatabase.Contains(mesh))) && claimed.Add(mesh))
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
            if (mesh != null && (IsOwnAsset(mesh) || (!AssetDatabase.Contains(owner) && !AssetDatabase.Contains(mesh))) && claimed.Add(mesh))
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

            var ownerPath = AssetDatabase.GetAssetPath(owner);
            var path = Path.GetDirectoryName(ownerPath).Replace('\\', '/') + "/" + owner.name + suffix + ".asset";
            mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null || !claimed.Add(mesh))
            {
                mesh = Create(Path.GetDirectoryName(ownerPath).Replace('\\', '/'), owner.name + suffix);
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

        // The meshes below the scene's own folder that neither the scene nor another scene uses any
        // more, such as the mesh of a lake that was removed again.
        public static List<Mesh> UnusedMeshes(Scene scene)
        {
            if (!IsSaved(scene))
            {
                return new List<Mesh>();
            }

            var used = new HashSet<Mesh>(scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<MeshFilter>(true))
                .Select(filter => filter.sharedMesh)
                .Where(mesh => mesh != null));
            return UnusedMeshes(DefaultDirectory(scene), used, scene.path);
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
            var elsewhere = new HashSet<string>(AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path != scenePath)
                .SelectMany(path => AssetDatabase.GetDependencies(path, false)));
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
