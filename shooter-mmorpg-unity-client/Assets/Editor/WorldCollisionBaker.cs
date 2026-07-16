using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShooterMmo.Collision;
using ShooterMmo.GameSimulation;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Editor
{
    public static class WorldCollisionBaker
    {
        public const string MenuPath = "Shooter MMO/Tools/World Collision/Bake Open Scene";

        [MenuItem(MenuPath)]
        public static void BakeOpenScene()
        {
            try
            {
                var authoring = FindAuthoringComponent();
                if (!authoring.TryValidate(out var validationError))
                {
                    throw new InvalidOperationException(validationError);
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
            var authoringDirectory = Path.Combine(repositoryRoot, "WorldData", "Authoring");
            var outputDirectory = Path.Combine(
                repositoryRoot,
                "WorldData",
                "Runtime",
                "Resources",
                "ShooterMmo",
                "WorldCollision",
                result.Manifest.WorldId);
            Directory.CreateDirectory(authoringDirectory);
            Directory.CreateDirectory(outputDirectory);

            var authoringPath = Path.Combine(
                authoringDirectory,
                result.Manifest.WorldId + ".collision-authoring.json");
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
