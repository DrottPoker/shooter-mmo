using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShooterMmo.Collision;
using ShooterMmo.GameSimulation;
using ShooterMmo.Worlds;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Editor
{
    public static class WorldCollisionBaker
    {
        public const string MenuPath = "Shooter MMO/Tools/World Collision/Bake Open Scene";
        public const string BakeBuildWorldScenesMenuPath =
            "Shooter MMO/Tools/World Collision/Bake Build World Scenes";

        [MenuItem(MenuPath)]
        public static void BakeOpenScene()
        {
            try
            {
                BakeActiveScene();
            }
            catch (Exception exception)
            {
                Debug.LogError("[WORLD COLLISION] Bake failed: " + exception.Message);
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateBakeOpenScene()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode
                && SceneManager.GetActiveScene().isLoaded;
        }

        [MenuItem(BakeBuildWorldScenesMenuPath)]
        public static void BakeBuildWorldScenes()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException(
                    "World collision cannot be baked while entering or running Play Mode.");
            }

            if (!Application.isBatchMode
                && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            var originalSetup = EditorSceneManager.GetSceneManagerSetup();
            TextAsset catalogAsset = null;
            try
            {
                catalogAsset = Resources.Load<TextAsset>(WorldSceneCatalog.ResourcePath);
                if (catalogAsset == null)
                {
                    throw new InvalidOperationException(
                        "The client world scene catalog resource is missing.");
                }

                if (!WorldSceneCatalog.TryParse(
                        catalogAsset.text,
                        out var catalog,
                        out var catalogError))
                {
                    throw new InvalidOperationException(catalogError);
                }

                var buildScenes = EditorBuildSettings.scenes
                    .Where(scene => scene.enabled)
                    .GroupBy(
                        scene => Path.GetFileNameWithoutExtension(scene.path),
                        StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(scene => scene.path).ToArray(),
                        StringComparer.Ordinal);
                foreach (var entry in catalog.entries)
                {
                    if (!buildScenes.TryGetValue(entry.sceneName, out var scenePaths)
                        || scenePaths.Length != 1)
                    {
                        throw new InvalidOperationException(
                            "World '" + entry.worldId + "' must map to exactly one enabled "
                            + "Build Profiles scene named '" + entry.sceneName + "'.");
                    }

                    EditorSceneManager.OpenScene(scenePaths[0], OpenSceneMode.Single);
                    BakeActiveScene(entry.worldId);
                }
            }
            finally
            {
                if (catalogAsset != null)
                {
                    Resources.UnloadAsset(catalogAsset);
                }

                if (originalSetup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(originalSetup);
                }
            }
        }

        private static CollisionWorldBakeResult BakeActiveScene(
            string expectedWorldId = null)
        {
            var authoring = FindAuthoringComponent();
            if (!authoring.TryValidate(out var validationError))
            {
                throw new InvalidOperationException(validationError);
            }

            if (!string.IsNullOrWhiteSpace(expectedWorldId)
                && !string.Equals(
                    authoring.WorldId.Trim(),
                    expectedWorldId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Scene '" + SceneManager.GetActiveScene().name + "' authored world '"
                    + authoring.WorldId.Trim() + "' instead of catalog world '"
                    + expectedWorldId + "'.");
            }

            var boxes = BuildBoxEntries(authoring).ToArray();
            var document = new CollisionWorldAuthoringDocument
            {
                FormatVersion = CollisionDataFormat.Version,
                WorldId = authoring.WorldId.Trim(),
                ChunkSize = authoring.ChunkSize,
                Boxes = boxes
            };
            var result = CollisionWorldCompiler.Compile(document);
            WriteBakeOutputs(document, result);
            AssetDatabase.Refresh();
            Debug.Log(
                "[WORLD COLLISION] Baked world '" + result.Manifest.WorldId
                + "' at revision " + result.Manifest.Revision
                + " with " + boxes.Length + " boxes in " + result.Chunks.Count
                + " chunks.");
            return result;
        }

        private static WorldCollisionAuthoring FindAuthoringComponent()
        {
            var components = UnityEngine.Object.FindObjectsByType<WorldCollisionAuthoring>(
                FindObjectsInactive.Include);
            if (components.Length != 1)
            {
                throw new InvalidOperationException(
                    "The open scene must contain exactly one WorldCollisionAuthoring component.");
            }

            if (components[0].gameObject.scene != SceneManager.GetActiveScene())
            {
                throw new InvalidOperationException(
                    "WorldCollisionAuthoring must belong to the active scene.");
            }

            return components[0];
        }

        private static IEnumerable<CollisionBoxAuthoringEntry> BuildBoxEntries(
            WorldCollisionAuthoring authoring)
        {
            var root = authoring.CollisionRoot;
            var colliders = root.GetComponentsInChildren<BoxCollider>(true)
                .Where(collider => collider.enabled
                    && collider.gameObject.activeInHierarchy
                    && !collider.isTrigger)
                .OrderBy(collider => GetStableSourcePath(collider), StringComparer.Ordinal)
                .ToArray();
            if (colliders.Length == 0)
            {
                throw new InvalidOperationException(
                    "The collision root does not contain any enabled non-trigger BoxColliders.");
            }

            var unsupported = root.GetComponentsInChildren<Collider>(true)
                .FirstOrDefault(collider => collider.enabled
                    && collider.gameObject.activeInHierarchy
                    && !collider.isTrigger
                    && !(collider is BoxCollider));
            if (unsupported != null)
            {
                throw new NotSupportedException(
                    "The current collision format supports BoxCollider authoring only. Unsupported collider: "
                    + GetHierarchyPath(unsupported.transform));
            }

            foreach (var collider in colliders)
            {
                var scale = collider.transform.lossyScale;
                if (Mathf.Approximately(scale.x, 0f)
                    || Mathf.Approximately(scale.y, 0f)
                    || Mathf.Approximately(scale.z, 0f))
                {
                    throw new InvalidOperationException(
                        "Collision transform has a zero scale: "
                        + GetHierarchyPath(collider.transform));
                }

                var center = collider.transform.TransformPoint(collider.center);
                var halfExtents = Vector3.Scale(collider.size * 0.5f, new Vector3(
                    Mathf.Abs(scale.x),
                    Mathf.Abs(scale.y),
                    Mathf.Abs(scale.z)));
                var rotation = collider.transform.rotation;
                yield return new CollisionBoxAuthoringEntry
                {
                    StableId = string.Empty,
                    SourcePath = GetStableSourcePath(collider),
                    LayerMask = authoring.LayerMask,
                    CenterX = center.x,
                    CenterY = center.y,
                    CenterZ = center.z,
                    HalfExtentX = halfExtents.x,
                    HalfExtentY = halfExtents.y,
                    HalfExtentZ = halfExtents.z,
                    RotationX = rotation.x,
                    RotationY = rotation.y,
                    RotationZ = rotation.z,
                    RotationW = rotation.w
                };
            }
        }

        private static void WriteBakeOutputs(
            CollisionWorldAuthoringDocument document,
            CollisionWorldBakeResult result)
        {
            var repositoryRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            var worldDirectory = Path.Combine(
                repositoryRoot,
                "WorldData",
                "Worlds",
                result.Manifest.WorldId);
            var authoringDirectory = Path.Combine(worldDirectory, "Authoring");
            var outputDirectory = Path.Combine(
                worldDirectory,
                "Runtime",
                "Resources",
                "ShooterMmo",
                "WorldCollision",
                result.Manifest.WorldId);
            Directory.CreateDirectory(authoringDirectory);
            Directory.CreateDirectory(outputDirectory);

            var authoringPath = Path.Combine(
                authoringDirectory,
                "collision.json");
            File.WriteAllText(authoringPath, JsonUtility.ToJson(document, true) + Environment.NewLine);
            File.WriteAllText(
                Path.Combine(outputDirectory, "manifest.json"),
                JsonUtility.ToJson(result.Manifest, true) + Environment.NewLine);

            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in result.Chunks)
            {
                var fileName = chunk.ManifestEntry.ResourceName + ".bytes";
                expectedFiles.Add(fileName);
                File.WriteAllBytes(Path.Combine(outputDirectory, fileName), chunk.Data);
            }

            foreach (var stalePath in Directory.EnumerateFiles(outputDirectory, "chunk_*.bytes"))
            {
                if (!expectedFiles.Contains(Path.GetFileName(stalePath)))
                {
                    File.Delete(stalePath);
                    var metaPath = stalePath + ".meta";
                    if (File.Exists(metaPath))
                    {
                        File.Delete(metaPath);
                    }
                }
            }
        }

        private static string GetStableSourcePath(BoxCollider collider)
        {
            var colliders = collider.GetComponents<BoxCollider>();
            var index = Array.IndexOf(colliders, collider);
            return collider.gameObject.scene.name + "/" + GetHierarchyPath(collider.transform)
                + "#BoxCollider" + index;
        }

        private static string GetHierarchyPath(Transform transform)
        {
            var names = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }
    }
}
