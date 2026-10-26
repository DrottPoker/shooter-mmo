using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mmorpg.Client.Core;
using Mmorpg.Client.Worlds;
using Mmorpg.GameSimulation;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Editor;
using Mmorpg.WorldData.Editor.Actors;
using UnityEditor;
using UnityEditor.AddressableAssets;
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
        public const string BakeWorldScenesMenuPath =
            "MMORPG/Tools/World Collision/Bake World Scenes";

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

        [MenuItem(BakeWorldScenesMenuPath)]
        public static void BakeWorldScenes()
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
            try
            {
                var catalogPath = ClientAddresses.ContentRoot + ClientAddresses.WorldSceneCatalog;
                var catalogAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(catalogPath);
                if (catalogAsset == null)
                {
                    throw new InvalidOperationException(
                        "The client world scene catalog is missing at " + catalogPath + ".");
                }

                if (!WorldSceneCatalog.TryParse(
                        catalogAsset.text,
                        out var catalog,
                        out var catalogError))
                {
                    throw new InvalidOperationException(catalogError);
                }

                var settings = AddressableAssetSettingsDefaultObject.Settings;
                var scenesGroup = settings == null
                    ? null
                    : settings.FindGroup(ClientAddressablesLayout.WorldScenesGroup);
                if (scenesGroup == null)
                {
                    throw new InvalidOperationException(
                        "The Addressables group '" + ClientAddressablesLayout.WorldScenesGroup
                        + "' is missing. Run " + ClientAddressablesLayout.ApplyMenuPath + ".");
                }

                var plans = PlanWorldBakes(
                    catalog.entries,
                    scenesGroup.entries.Select(entry => entry.AssetPath));
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
                if (originalSetup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(originalSetup);
                }
            }
        }

        // Groups catalog Worlds by their Addressables World scene, so a reusable scene shared
        // by several Worlds is opened once and baked once for each of them.
        public static IReadOnlyList<WorldSceneBakePlan> PlanWorldBakes(
            IEnumerable<WorldSceneCatalogEntry> entries,
            IEnumerable<string> worldScenePaths)
        {
            var worldScenes = worldScenePaths
                .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToArray(),
                    StringComparer.Ordinal);
            var plans = new List<WorldSceneBakePlan>();
            var plansByScene = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!worldScenes.TryGetValue(entry.sceneName, out var scenePaths)
                    || scenePaths.Length != 1)
                {
                    throw new InvalidOperationException(
                        "World '" + entry.worldId + "' must map to exactly one Addressables "
                        + "World scene named '" + entry.sceneName + "'.");
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

            var colliders = FindBakedColliders(authoring.CollisionRoot);
            var boxes = BuildBoxEntries(authoring, colliders.OfType<BoxCollider>()).ToArray();
            var meshes = BuildMeshEntries(authoring, colliders.OfType<MeshCollider>()).ToArray();
            var heightfields = BuildHeightfieldEntries(
                authoring,
                colliders.OfType<TerrainCollider>()).ToArray();
            var document = new CollisionWorldAuthoringDocument
            {
                FormatVersion = CollisionDataFormat.Version,
                WorldId = string.IsNullOrWhiteSpace(targetWorldId)
                    ? authoredWorldId
                    : targetWorldId,
                ChunkSize = authoring.ChunkSize,
                Boxes = boxes,
                Meshes = meshes,
                Heightfields = heightfields
            };
            // The authoring file keeps the scene; the chunks also hold the resource nodes.
            var authoringJson = JsonUtility.ToJson(document, true) + Environment.NewLine;
            var persistedDocument = JsonUtility.FromJson<CollisionWorldAuthoringDocument>(
                authoringJson);
            var resourceNodes = ResourceNodeBoxes(persistedDocument.WorldId);
            persistedDocument.Boxes = persistedDocument.Boxes.Concat(resourceNodes).ToArray();
            var result = CollisionWorldCompiler.Compile(persistedDocument);
            WriteBakeOutputs(authoringJson, result);
            AssetDatabase.Refresh();
            Debug.Log(
                "[WORLD COLLISION] Baked world '" + result.Manifest.WorldId
                + "' at revision " + result.Manifest.Revision
                + " with " + boxes.Length + " boxes, " + meshes.Length + " meshes, and "
                + heightfields.Length + " heightfields in " + result.Chunks.Count
                + " chunks, and " + resourceNodes.Length + " resource node boxes.");
            return result;
        }

        // The World's resource nodes block movement where they stand, as WorldCollisionCompiler
        // adds them from the compiled World actors. The bake compiles the actors from their
        // authoring and requires the compiled file's revision, so both read the same actors.
        public static CollisionBoxAuthoringEntry[] ResourceNodeBoxes(string worldId)
        {
            var workspace = new WorldActorEditorService(WorldActorEditorPaths.CreateDefault(worldId)).Load();
            var runtime = WorldActorCompiler.Compile(
                workspace.Catalog.ToDomain(),
                workspace.Spawns.ToDomain());
            if (!string.Equals(runtime.Revision, workspace.CurrentRuntime.revision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The World actors of '" + worldId + "' changed since they were compiled. "
                    + "Save them in Actor Studio or Spawn Authoring, or run WorldActorCompiler, before baking.");
            }

            return WorldActorCollision.Boxes(runtime)
                .Select(box => CollisionBoxAuthoringEntry.MovementObstacle(
                    box.SourcePath,
                    box.CenterX,
                    box.CenterY,
                    box.CenterZ,
                    box.HalfExtentX,
                    box.HalfExtentY,
                    box.HalfExtentZ))
                .ToArray();
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

        // Boxes, non-convex meshes, and terrains bake. Any other enabled solid collider
        // stops the bake, so authored collision never silently goes missing.
        private static Collider[] FindBakedColliders(Transform root)
        {
            var colliders = root.GetComponentsInChildren<Collider>(true)
                .Where(collider => collider.enabled
                    && collider.gameObject.activeInHierarchy
                    && !collider.isTrigger)
                .OrderBy(collider => GetStableSourcePath(collider), StringComparer.Ordinal)
                .ToArray();
            if (colliders.Length == 0)
            {
                throw new InvalidOperationException(
                    "The collision root does not contain any enabled non-trigger colliders.");
            }

            var unsupported = colliders.FirstOrDefault(collider => !(collider is BoxCollider)
                && !(collider is MeshCollider)
                && !(collider is TerrainCollider));
            if (unsupported != null)
            {
                throw new NotSupportedException(
                    "The collision format supports BoxCollider, MeshCollider, and TerrainCollider "
                    + "authoring only. Unsupported collider: "
                    + GetHierarchyPath(unsupported.transform));
            }

            return colliders;
        }

        private static IEnumerable<CollisionBoxAuthoringEntry> BuildBoxEntries(
            WorldCollisionAuthoring authoring,
            IEnumerable<BoxCollider> colliders)
        {
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

        // Each mesh triangle becomes a one-sided collision triangle in world space that
        // faces the same way as its rendered front face.
        private static IEnumerable<CollisionMeshAuthoringEntry> BuildMeshEntries(
            WorldCollisionAuthoring authoring,
            IEnumerable<MeshCollider> colliders)
        {
            foreach (var collider in colliders)
            {
                if (collider.convex)
                {
                    throw new NotSupportedException(
                        "Convex MeshColliders cannot bake, because collision meshes are one-sided "
                        + "triangles. Clear Convex on: " + GetHierarchyPath(collider.transform));
                }

                var mesh = collider.sharedMesh;
                if (mesh == null)
                {
                    throw new InvalidOperationException(
                        "MeshCollider has no mesh: " + GetHierarchyPath(collider.transform));
                }

                var localToWorld = collider.transform.localToWorldMatrix;
                var vertices = new List<float>(mesh.vertexCount * 3);
                foreach (var vertex in mesh.vertices)
                {
                    var position = localToWorld.MultiplyPoint3x4(vertex);
                    vertices.Add(position.x);
                    vertices.Add(position.y);
                    vertices.Add(position.z);
                }

                // A mirroring transform reverses the winding, so swap two corners back.
                var triangles = mesh.triangles;
                if (localToWorld.determinant < 0f)
                {
                    for (var index = 0; index < triangles.Length; index += 3)
                    {
                        var swap = triangles[index + 1];
                        triangles[index + 1] = triangles[index + 2];
                        triangles[index + 2] = swap;
                    }
                }

                yield return new CollisionMeshAuthoringEntry
                {
                    StableId = string.Empty,
                    SourcePath = GetStableSourcePath(collider),
                    LayerMask = authoring.LayerMask,
                    Vertices = CollisionAuthoringEncoding.EncodeFloats(vertices),
                    Triangles = CollisionAuthoringEncoding.EncodeInts(triangles)
                };
            }
        }

        // A terrain bakes as a world-space heightfield with one sample per heightmap texel
        // and one hole flag per heightmap cell.
        private static IEnumerable<CollisionHeightfieldAuthoringEntry> BuildHeightfieldEntries(
            WorldCollisionAuthoring authoring,
            IEnumerable<TerrainCollider> colliders)
        {
            foreach (var collider in colliders)
            {
                var terrainData = collider.terrainData;
                if (terrainData == null)
                {
                    throw new InvalidOperationException(
                        "TerrainCollider has no terrain data: " + GetHierarchyPath(collider.transform));
                }

                if (collider.transform.rotation != Quaternion.identity
                    || collider.transform.lossyScale != Vector3.one)
                {
                    throw new InvalidOperationException(
                        "A terrain must not be rotated or scaled: "
                        + GetHierarchyPath(collider.transform));
                }

                var resolution = terrainData.heightmapResolution;
                var size = terrainData.size;
                var origin = collider.transform.position;
                var normalizedHeights = terrainData.GetHeights(0, 0, resolution, resolution);
                var heights = new float[resolution * resolution];
                for (var z = 0; z < resolution; z++)
                {
                    for (var x = 0; x < resolution; x++)
                    {
                        heights[(z * resolution) + x] = origin.y + (normalizedHeights[z, x] * size.y);
                    }
                }

                var cells = resolution - 1;
                var solid = terrainData.GetHoles(0, 0, cells, cells);
                var holes = new byte[cells * cells];
                var hasHoles = false;
                for (var z = 0; z < cells; z++)
                {
                    for (var x = 0; x < cells; x++)
                    {
                        if (!solid[z, x])
                        {
                            holes[(z * cells) + x] = 1;
                            hasHoles = true;
                        }
                    }
                }

                yield return new CollisionHeightfieldAuthoringEntry
                {
                    StableId = string.Empty,
                    SourcePath = GetStableSourcePath(collider),
                    LayerMask = authoring.LayerMask,
                    OriginX = origin.x,
                    OriginZ = origin.z,
                    SpacingX = size.x / cells,
                    SpacingZ = size.z / cells,
                    SamplesX = resolution,
                    SamplesZ = resolution,
                    Heights = CollisionAuthoringEncoding.EncodeFloats(heights),
                    Holes = hasHoles ? CollisionAuthoringEncoding.EncodeBytes(holes) : string.Empty
                };
            }
        }

        private static void WriteBakeOutputs(
            string authoringJson,
            CollisionWorldBakeResult result)
        {
            var repositoryRoot = RepositoryLayout.FindRoot();
            var authoringPath = RepositoryLayout.CollisionSourcePath(
                repositoryRoot,
                result.Manifest.WorldId);
            var outputDirectory = RepositoryLayout.GeneratedCollisionDirectory(
                repositoryRoot,
                result.Manifest.WorldId);
            Directory.CreateDirectory(Path.GetDirectoryName(authoringPath));
            Directory.CreateDirectory(outputDirectory);

            // Chunks first and the manifest after them, so a reader never sees a manifest
            // whose checksums point at chunks that were not written.
            var writes = new List<KeyValuePair<string, byte[]>>();
            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in result.Chunks)
            {
                var fileName = chunk.ManifestEntry.ResourceName + CollisionDataFormat.ChunkFileExtension;
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

            foreach (var stalePath in Directory.EnumerateFiles(outputDirectory, "chunk_*" + CollisionDataFormat.ChunkFileExtension))
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

        // Boxes keep their original path form, so existing box ids stay stable.
        private static string GetStableSourcePath(Collider collider)
        {
            var colliders = collider.GetComponents(collider.GetType());
            var index = Array.IndexOf(colliders, collider);
            return collider.gameObject.scene.name + "/" + GetHierarchyPath(collider.transform)
                + "#" + collider.GetType().Name + index;
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
