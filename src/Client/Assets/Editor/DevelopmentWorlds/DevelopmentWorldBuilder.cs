using System;
using System.IO;
using Mmorpg.Client.Worlds;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor;
using Mmorpg.WorldData.Worlds;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Editor
{
    // Shared greybox toolkit for the development World builders. A builder replaces every
    // child of the scene's Environment root and places PlayerSpawn from the World manifest,
    // so the manifest stays the only source of the spawn and the playable bounds. Gameplay
    // and actor content are never changed.
    internal sealed class DevelopmentWorldBuilder
    {
        public const float ChunkSize = 32f;
        public const long CollisionLayerMask = 3L;

        // Movement limits of the shared simulation, so stations sit just inside or outside them.
        public const float StepHeight = 0.35f;
        public const float MaximumSlopeDegrees = 45f;

        private const float RampThickness = 0.6f;
        private const float RampBurialDepth = 0.1f;
        private const float DecalHeight = 0.01f;

        // Label canvases use 4 mm units. A TextMesh Pro line of the UI font is about 1.15 times
        // its font size tall.
        private const float LabelMetersPerUnit = 0.004f;
        private const float LabelLineSpacing = 1.15f;

        private DevelopmentWorldBuilder(
            Scene scene,
            GameObject environment,
            WorldManifestDocument manifest,
            DevelopmentWorldPalette palette)
        {
            Scene = scene;
            Environment = environment.transform;
            Manifest = manifest;
            Palette = palette;
        }

        public Scene Scene { get; }

        public Transform Environment { get; }

        public WorldManifestDocument Manifest { get; }

        public DevelopmentWorldPalette Palette { get; }

        // Opens the scene, configures its collision authoring for the World, clears the
        // Environment root, and places PlayerSpawn. Returns null when the user keeps unsaved
        // scene changes.
        public static DevelopmentWorldBuilder Begin(
            string scenePath,
            string worldId,
            float groundSize)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException(
                    "A development World cannot be rebuilt while entering or running Play Mode.");
            }

            // Opening the scene replaces every open scene, so unsaved edits must be saved or
            // explicitly discarded by the user first.
            if (!Application.isBatchMode
                && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return null;
            }

            var manifest = LoadManifest(worldId);
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var environment = FindGameObject(scene, "Environment");
            var spawn = FindGameObject(scene, "PlayerSpawn");
            if (environment == null || spawn == null)
            {
                throw new InvalidOperationException(
                    scenePath + " must contain an Environment root and a Gameplay/PlayerSpawn.");
            }

            ConfigureAuthoring(environment, worldId);
            for (var index = environment.transform.childCount - 1; index >= 0; index--)
            {
                UnityEngine.Object.DestroyImmediate(environment.transform.GetChild(index).gameObject);
            }

            spawn.transform.localPosition = new Vector3(
                manifest.spawn.x,
                manifest.spawn.y,
                manifest.spawn.z);
            spawn.transform.localRotation = Quaternion.Euler(0f, manifest.spawn.yawDegrees, 0f);
            return new DevelopmentWorldBuilder(
                scene,
                environment,
                manifest,
                DevelopmentWorldPalette.Create(worldId, groundSize));
        }

        public static WorldManifestDocument LoadManifest(string worldId)
        {
            var path = Path.Combine(
                ContentLayout.WorldDirectory(
                    RepositoryLayout.ContentRoot(RepositoryLayout.FindRoot()),
                    worldId),
                WorldManifestFormat.FileName);
            var manifest = JsonUtility.FromJson<WorldManifestDocument>(File.ReadAllText(path));
            WorldManifestValidator.Validate(manifest);
            if (!string.Equals(manifest.worldId, worldId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(path + " must declare World '" + worldId + "'.");
            }

            return manifest;
        }

        public void Finish(string logTag)
        {
            foreach (var child in Environment.GetComponentsInChildren<Transform>(true))
            {
                child.gameObject.isStatic = true;
            }

            if (!EditorSceneManager.SaveScene(Scene))
            {
                throw new InvalidOperationException(Scene.path + " could not be saved after rebuilding.");
            }

            AssetDatabase.SaveAssets();
            Debug.Log(
                "[" + logTag + "] Rebuilt the environment with "
                + Environment.GetComponentsInChildren<BoxCollider>(true).Length
                + " collision boxes, "
                + Environment.GetComponentsInChildren<MeshCollider>(true).Length
                + " collision meshes, and "
                + Environment.GetComponentsInChildren<TerrainCollider>(true).Length
                + " terrains. Actor content was not changed.");
        }

        // A one meter thick boundary wall whose inner faces lie on the manifest bounds.
        public void BuildGroundAndBoundaries(float groundSize, float wallHeight)
        {
            Box(Environment, "Ground", new Vector3(0f, -0.25f, 0f),
                new Vector3(groundSize, 0.5f, groundSize), Palette.Ground);
            var boundaries = Group(Environment, "Boundaries");
            var bounds = Manifest.bounds;
            var width = bounds.maximumX - bounds.minimumX + 2f;
            var depth = bounds.maximumZ - bounds.minimumZ + 2f;
            var centerX = (bounds.minimumX + bounds.maximumX) * 0.5f;
            var centerZ = (bounds.minimumZ + bounds.maximumZ) * 0.5f;
            Box(boundaries, "NorthWall", new Vector3(centerX, wallHeight * 0.5f, bounds.maximumZ + 0.5f),
                new Vector3(width, wallHeight, 1f), Palette.Boundary);
            Box(boundaries, "SouthWall", new Vector3(centerX, wallHeight * 0.5f, bounds.minimumZ - 0.5f),
                new Vector3(width, wallHeight, 1f), Palette.Boundary);
            Box(boundaries, "EastWall", new Vector3(bounds.maximumX + 0.5f, wallHeight * 0.5f, centerZ),
                new Vector3(1f, wallHeight, depth), Palette.Boundary);
            Box(boundaries, "WestWall", new Vector3(bounds.minimumX - 0.5f, wallHeight * 0.5f, centerZ),
                new Vector3(1f, wallHeight, depth), Palette.Boundary);
        }

        public Transform Group(Transform parent, string name, Vector3 position = default, float yaw = 0f)
        {
            var group = new GameObject(name)
            {
                isStatic = true
            };
            group.transform.SetParent(parent, false);
            group.transform.localPosition = position;
            group.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            return group.transform;
        }

        // A box from a cube primitive. Collidable boxes bake into the authoritative collision.
        public GameObject Box(
            Transform parent,
            string name,
            Vector3 center,
            Vector3 size,
            Material material,
            bool collidable = true,
            float yaw = 0f)
        {
            return Box(parent, name, center, size, material, collidable, Quaternion.Euler(0f, yaw, 0f));
        }

        public GameObject Box(
            Transform parent,
            string name,
            Vector3 center,
            Vector3 size,
            Material material,
            bool collidable,
            Quaternion rotation)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.isStatic = true;
            box.transform.SetParent(parent, false);
            box.transform.localPosition = center;
            box.transform.localRotation = rotation;
            box.transform.localScale = size;
            box.GetComponent<MeshRenderer>().sharedMaterial = material;
            if (!collidable)
            {
                UnityEngine.Object.DestroyImmediate(box.GetComponent<BoxCollider>());
            }

            return box;
        }

        // A flat visual-only surface, such as a path or a plaza, lifted by layer so
        // overlapping decals never flicker.
        public GameObject Decal(
            Transform parent,
            string name,
            Vector2 center,
            Vector2 size,
            Material material,
            int layer = 0,
            float yaw = 0f)
        {
            var thickness = 0.02f;
            return Box(
                parent,
                name,
                new Vector3(center.x, (DecalHeight * (layer + 1)) - (thickness * 0.5f), center.y),
                new Vector3(size.x, thickness, size.y),
                material,
                false,
                yaw);
        }

        // A box whose top is a walkable slope rising along local +Z. Its top surface starts
        // just below the ground and ends exactly at the top edge of the face it leads onto.
        public GameObject Ramp(
            Transform parent,
            string name,
            float centerX,
            float width,
            float topFaceZ,
            float topHeight,
            float slopeDegrees,
            Material material)
        {
            var slope = slopeDegrees * Mathf.Deg2Rad;
            var rise = topHeight + RampBurialDepth;
            var surfaceCenter = new Vector3(
                centerX,
                topHeight - (rise * 0.5f),
                topFaceZ - (rise * 0.5f / Mathf.Tan(slope)));
            var surfaceNormal = new Vector3(0f, Mathf.Cos(slope), -Mathf.Sin(slope));
            return Box(
                parent,
                name,
                surfaceCenter - (surfaceNormal * (RampThickness * 0.5f)),
                new Vector3(width, RampThickness, rise / Mathf.Sin(slope)),
                material,
                true,
                Quaternion.Euler(-slopeDegrees, 0f, 0f));
        }

        // Solid steps rising along local +Z from startZ, each a full-height block.
        public void Stairs(
            Transform parent,
            string name,
            float centerX,
            float width,
            float startZ,
            int stepCount,
            float rise,
            float tread,
            Material material)
        {
            for (var index = 0; index < stepCount; index++)
            {
                var top = rise * (index + 1);
                Box(
                    parent,
                    name + (index + 1).ToString("00"),
                    new Vector3(centerX, top * 0.5f, startZ + (tread * (index + 0.5f))),
                    new Vector3(width, top, tread),
                    material);
            }
        }

        // A two-sided board between two posts, so no post hides the text. The posts collide;
        // the board hangs above the character height and is visual only.
        public void Sign(Transform parent, string name, Vector3 position, float yaw, string text)
        {
            var sign = Group(parent, name, position, yaw);
            Box(sign, "WestPost", new Vector3(-1.72f, 1.6f, 0f), new Vector3(0.2f, 3.2f, 0.2f), Palette.SignPost);
            Box(sign, "EastPost", new Vector3(1.72f, 1.6f, 0f), new Vector3(0.2f, 3.2f, 0.2f), Palette.SignPost);
            Box(sign, "Board", new Vector3(0f, 2.75f, 0f), new Vector3(3.2f, 0.9f, 0.12f), Palette.SignBoard, false);
            Label(sign, "FrontText", new Vector3(0f, 2.75f, -0.07f), Quaternion.identity,
                new Vector2(3.1f, 0.85f), text, 0.22f, Color.white);
            Label(sign, "BackText", new Vector3(0f, 2.75f, 0.07f), Quaternion.Euler(0f, 180f, 0f),
                new Vector2(3.1f, 0.85f), text, 0.22f, Color.white);
        }

        // Text painted flat on the ground, readable when facing local +Z.
        public void FloorLabel(Transform parent, string name, Vector2 position, float yaw, string text, float lineHeight = 0.5f)
        {
            Label(
                parent,
                name,
                new Vector3(position.x, DecalHeight * 4f, position.y),
                Quaternion.Euler(90f, yaw, 0f),
                new Vector2(lineHeight * 16f, lineHeight * 3f),
                text,
                lineHeight,
                new Color(0.95f, 0.95f, 0.9f));
        }

        // A world-space canvas with one TextMesh Pro text in the UI font, sharp at any distance.
        // World-space UI is depth tested, so labels hide behind walls, and it has no raycaster,
        // so it never takes pointer input.
        private static void Label(
            Transform parent,
            string name,
            Vector3 position,
            Quaternion rotation,
            Vector2 size,
            string text,
            float lineHeight,
            Color color)
        {
            var label = new GameObject(name, typeof(RectTransform))
            {
                isStatic = true
            };
            var rect = (RectTransform)label.transform;
            rect.SetParent(parent, false);
            rect.localPosition = position;
            rect.localRotation = rotation;
            rect.localScale = Vector3.one * LabelMetersPerUnit;
            rect.sizeDelta = size / LabelMetersPerUnit;
            var canvas = label.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var textObject = new GameObject("Text", typeof(RectTransform))
            {
                isStatic = true
            };
            var textRect = (RectTransform)textObject.transform;
            textRect.SetParent(rect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            var uiText = textObject.AddComponent<TextMeshProUGUI>();
            uiText.font = TMP_Settings.defaultFontAsset;
            uiText.fontSize = lineHeight / LabelMetersPerUnit / LabelLineSpacing;
            uiText.alignment = TextAlignmentOptions.Center;
            uiText.textWrappingMode = TextWrappingModes.NoWrap;
            uiText.overflowMode = TextOverflowModes.Overflow;
            uiText.richText = false;
            uiText.color = color;
            uiText.raycastTarget = false;
            uiText.text = text;
        }
        private static void ConfigureAuthoring(GameObject environment, string worldId)
        {
            var authoring = environment.GetComponent<WorldCollisionAuthoring>();
            if (authoring == null)
            {
                authoring = environment.AddComponent<WorldCollisionAuthoring>();
            }

            var serializedAuthoring = new SerializedObject(authoring);
            serializedAuthoring.FindProperty("worldId").stringValue = worldId;
            serializedAuthoring.FindProperty("chunkSize").floatValue = ChunkSize;
            serializedAuthoring.FindProperty("collisionRoot").objectReferenceValue = environment.transform;
            serializedAuthoring.FindProperty("layerMask").longValue = CollisionLayerMask;
            serializedAuthoring.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject FindGameObject(Scene scene, string objectName)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    if (string.Equals(transform.name, objectName, StringComparison.Ordinal))
                    {
                        return transform.gameObject;
                    }
                }
            }

            return null;
        }
    }
}
