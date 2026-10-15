using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mmorpg.Client.Collision;
using Mmorpg.GameSimulation;
using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Editor
{
    public sealed class WorldSceneBakePlan
    {
        public WorldSceneBakePlan(string scenePath, IReadOnlyList<string> worldIds)
        {
            ScenePath = scenePath;
            WorldIds = worldIds;
        }

        public string ScenePath { get; }

        public IReadOnlyList<string> WorldIds { get; }
    }

    public static class WorldCollisionBaker
    {
        public const string MenuPath = "MMORPG/Tools/World Collision/Bake Open Scene";
        public const string BakeBuildWorldScenesMenuPath =
            "MMORPG/Tools/World Collision/Bake Build World Scenes";

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

                var plans = PlanBuildWorldBakes(
                    catalog.entries,
                    EditorBuildSettings.scenes
                        .Where(scene => scene.enabled)
                        .Select(scene => scene.path));
                foreach (var plan in plans)
                {
                    EditorSceneManager.OpenScene(plan.ScenePath, OpenSceneMode.Single);
                    foreach (var worldId in plan.WorldIds)
                    {
                        BakeActiveScene(worldId, plan.WorldIds);
                    }
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

        // Groups catalog Worlds by their Build Profiles scene, so a reusable scene shared
        // by several Worlds is opened once and baked once for each of them.
        public static IReadOnlyList<WorldSceneBakePlan> PlanBuildWorldBakes(
            IEnumerable<WorldSceneCatalogEntry> entries,
            IEnumerable<string> enabledScenePaths)
        {
            var buildScenes = enabledScenePaths
                .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToArray(),
                    StringComparer.Ordinal);
            var plans = new List<WorldSceneBakePlan>();
            var plansByScene = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!buildScenes.TryGetValue(entry.sceneName, out var scenePaths)
                    || scenePaths.Length != 1)
                {
                    throw new InvalidOperationException(
                        "World '" + entry.worldId + "' must map to exactly one enabled "
                        + "Build Profiles scene named '" + entry.sceneName + "'.");
                }

                if (!plansByScene.TryGetValue(scenePaths[0], out var worldIds))
                {
                    worldIds = new List<string>();
                    plansByScene.Add(scenePaths[0], worldIds);
                    plans.Add(new WorldSceneBakePlan(scenePaths[0], worldIds));
                }

                worldIds.Add(entry.worldId);
            }

            return plans;
        }

        // The authored World id names the scene's own World. A reusable scene is baked
        // for every catalog World that uses it, so the authored id must be one of them.
        private static CollisionWorldBakeResult BakeActiveScene(
            string targetWorldId = null,
            IReadOnlyCollection<string> sceneWorldIds = null)
        {
            var authoring = FindAuthoringComponent();
            if (!authoring.TryValidate(out var validationError))
            {
                throw new InvalidOperationException(validationError);
            }

            var authoredWorldId = authoring.WorldId.Trim();
            if (sceneWorldIds != null
                && !sceneWorldIds.Contains(authoredWorldId, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "Scene '" + SceneManager.GetActiveScene().name + "' authored world '"
                    + authoredWorldId + "', which is not one of its catalog worlds '"
                    + string.Join("', '", sceneWorldIds) + "'.");
            }

            var boxes = BuildBoxEntries(authoring).ToArray();
            var document = new CollisionWorldAuthoringDocument
            {
                FormatVersion = CollisionDataFormat.Version,
                WorldId = string.IsNullOrWhiteSpace(targetWorldId)
                    ? authoredWorldId
                    : targetWorldId,
                ChunkSize = authoring.ChunkSize,
                Boxes = boxes
            };
            var authoringJson = JsonUtility.ToJson(document, true) + Environment.NewLine;
            var persistedDocument = JsonUtility.FromJson<CollisionWorldAuthoringDocument>(
                authoringJson);
            var result = CollisionWorldCompiler.Compile(persistedDocument);
            WriteBakeOutputs(authoringJson, result);
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
            string authoringJson,
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
                "WorldCollision",
                result.Manifest.WorldId);
            Directory.CreateDirectory(authoringDirectory);
            Directory.CreateDirectory(outputDirectory);

            var authoringPath = Path.Combine(
                authoringDirectory,
                "collision.json");

            // Chunks first and the manifest after them, so a reader never sees a manifest
            // whose checksums point at chunks that were not written.
            var writes = new List<KeyValuePair<string, byte[]>>();
            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in result.Chunks)
            {
                var fileName = chunk.ManifestEntry.ResourceName + ".bytes";
                expectedFiles.Add(fileName);
                writes.Add(new KeyValuePair<string, byte[]>(
                    Path.Combine(outputDirectory, fileName),
                    chunk.Data));
            }

            writes.Add(new KeyValuePair<string, byte[]>(
                Path.Combine(outputDirectory, "manifest.json"),
                Encoding.UTF8.GetBytes(JsonUtility.ToJson(result.Manifest, true) + Environment.NewLine)));
            writes.Add(new KeyValuePair<string, byte[]>(
                authoringPath,
                Encoding.UTF8.GetBytes(authoringJson)));
            ReplaceFilesAtomically(writes);

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

        // Writes every file to a temporary sibling first and then swaps them in with
        // backups, restoring the previous files if any swap fails. Unity ignores .tmp
        // files, so partial output is never imported.
        private static void ReplaceFilesAtomically(IReadOnlyList<KeyValuePair<string, byte[]>> writes)
        {
            var temporaryPaths = new List<string>();
            var committed = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (var write in writes)
                {
                    var temporaryPath = write.Key + ".bake.tmp";
                    temporaryPaths.Add(temporaryPath);
                    File.WriteAllBytes(temporaryPath, write.Value);
                }

                for (var index = 0; index < writes.Count; index++)
                {
                    var target = writes[index].Key;
                    var backupPath = target + ".backup.tmp";
                    if (File.Exists(target))
                    {
                        File.Replace(temporaryPaths[index], target, backupPath);
                        committed.Add(new KeyValuePair<string, string>(target, backupPath));
                    }
                    else
                    {
                        File.Move(temporaryPaths[index], target);
                        committed.Add(new KeyValuePair<string, string>(target, null));
                    }
                }
            }
            catch
            {
                for (var index = committed.Count - 1; index >= 0; index--)
                {
                    var target = committed[index].Key;
                    var backupPath = committed[index].Value;
                    if (backupPath == null)
                    {
                        File.Delete(target);
                    }
                    else
                    {
                        File.Replace(backupPath, target, null);
                    }
                }

                throw;
            }
            finally
            {
                foreach (var temporaryPath in temporaryPaths)
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }

            foreach (var entry in committed)
            {
                if (entry.Value != null && File.Exists(entry.Value))
                {
                    File.Delete(entry.Value);
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
