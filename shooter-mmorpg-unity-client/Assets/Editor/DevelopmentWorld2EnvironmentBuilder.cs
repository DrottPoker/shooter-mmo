using System;
using ShooterMmo.Collision;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Editor
{
    public static class DevelopmentWorld2EnvironmentBuilder
    {
        public const string MenuPath =
            "Shooter MMO/Tools/Development Worlds/Rebuild Development World 2 Environment";
        private const string ScenePath = "Assets/Scenes/DevelopmentWorld2.unity";
        private const string MaterialDirectory =
            "Assets/Art/Materials/DevelopmentWorld2";

        [MenuItem(MenuPath)]
        public static void RebuildFromMenu()
        {
            if (!EditorUtility.DisplayDialog(
                    "Rebuild Development World 2",
                    "This replaces every child under the Environment root. Gameplay and actor content are not changed.",
                    "Rebuild",
                    "Cancel"))
            {
                return;
            }

            RebuildForAutomation();
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateRebuildFromMenu()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        public static void RebuildForAutomation()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException(
                    "Development World 2 cannot be rebuilt while entering or running Play Mode.");
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var environment = FindGameObject(scene, "Environment");
            if (environment == null)
            {
                throw new InvalidOperationException(
                    "Development World 2 must contain an Environment root.");
            }

            ConfigureAuthoring(environment);
            ClearChildren(environment.transform);

            var palette = CreatePalette();
            BuildGroundAndBoundaries(environment.transform, palette);
            BuildCentralHub(environment.transform, palette);
            BuildNorthTraversal(environment.transform, palette);
            BuildEastSightline(environment.transform, palette);
            BuildSouthwestWilderness(environment.transform, palette);
            BuildSoutheastExpansion(environment.transform, palette);
            BuildRouteGuides(environment.transform, palette);

            SetStaticRecursively(environment);
            if (!EditorSceneManager.SaveScene(scene))
            {
                throw new InvalidOperationException(
                    "Development World 2 could not be saved after rebuilding the environment.");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(
                "[DEVELOPMENT WORLD 2] Rebuilt the environment with "
                + environment.GetComponentsInChildren<BoxCollider>(true).Length
                + " authoritative collision boxes. Actor content was not changed.");
        }

        private static void ConfigureAuthoring(GameObject environment)
        {
            var authoring = environment.GetComponent<WorldCollisionAuthoring>();
            if (authoring == null)
            {
                authoring = environment.AddComponent<WorldCollisionAuthoring>();
            }

            var serializedAuthoring = new SerializedObject(authoring);
            serializedAuthoring.FindProperty("worldId").stringValue =
                "development-world-2";
            serializedAuthoring.FindProperty("chunkSize").floatValue = 32f;
            serializedAuthoring.FindProperty("collisionRoot").objectReferenceValue =
                environment.transform;
            serializedAuthoring.FindProperty("layerMask").longValue = 3L;
            serializedAuthoring.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ClearChildren(Transform parent)
        {
            for (var index = parent.childCount - 1; index >= 0; index--)
            {
                UnityEngine.Object.DestroyImmediate(parent.GetChild(index).gameObject);
            }
        }

        private static EnvironmentPalette CreatePalette()
        {
            EnsureDirectory("Assets/Art/Materials", "DevelopmentWorld2");
            return new EnvironmentPalette(
                CreateOrUpdateMaterial(
                    "M_DevelopmentWorld2_Ground",
                    new Color(0.19f, 0.28f, 0.20f)),
                CreateOrUpdateMaterial(
                    "M_DevelopmentWorld2_Road",
                    new Color(0.23f, 0.25f, 0.27f)),
                CreateOrUpdateMaterial(
                    "M_DevelopmentWorld2_Structure",
                    new Color(0.28f, 0.38f, 0.46f)),
                CreateOrUpdateMaterial(
                    "M_DevelopmentWorld2_Cover",
                    new Color(0.68f, 0.42f, 0.16f)),
                CreateOrUpdateMaterial(
                    "M_DevelopmentWorld2_Wilderness",
                    new Color(0.35f, 0.31f, 0.26f)),
                CreateOrUpdateMaterial(
                    "M_DevelopmentWorld2_Expansion",
                    new Color(0.25f, 0.43f, 0.40f)),
                CreateOrUpdateMaterial(
                    "M_DevelopmentWorld2_Boundary",
                    new Color(0.12f, 0.14f, 0.16f)),
                CreateOrUpdateMaterial(
                    "M_DevelopmentWorld2_ServiceMarker",
                    new Color(0.20f, 0.62f, 0.72f)));
        }

        private static void EnsureDirectory(string parent, string child)
        {
            var path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }

        private static Material CreateOrUpdateMaterial(string name, Color color)
        {
            var path = MaterialDirectory + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                {
                    throw new InvalidOperationException(
                        "The Universal Render Pipeline Lit shader is unavailable.");
                }

                material = new Material(shader)
                {
                    name = name
                };
                AssetDatabase.CreateAsset(material, path);
            }

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }

            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", 0.18f);
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        private static void BuildGroundAndBoundaries(
            Transform environment,
            EnvironmentPalette palette)
        {
            CreateBox(
                environment,
                "Ground",
                new Vector3(0f, -0.25f, 0f),
                new Vector3(512f, 0.5f, 512f),
                palette.Ground);

            var boundaries = CreateGroup(environment, "Boundaries");
            CreateBox(
                boundaries,
                "NorthWall",
                new Vector3(0f, 3f, 255f),
                new Vector3(512f, 6f, 2f),
                palette.Boundary);
            CreateBox(
                boundaries,
                "SouthWall",
                new Vector3(0f, 3f, -255f),
                new Vector3(512f, 6f, 2f),
                palette.Boundary);
            CreateBox(
                boundaries,
                "EastWall",
                new Vector3(255f, 3f, 0f),
                new Vector3(2f, 6f, 512f),
                palette.Boundary);
            CreateBox(
                boundaries,
                "WestWall",
                new Vector3(-255f, 3f, 0f),
                new Vector3(2f, 6f, 512f),
                palette.Boundary);
        }

        private static void BuildCentralHub(
            Transform environment,
            EnvironmentPalette palette)
        {
            var hub = CreateGroup(environment, "CentralHub");
            CreateBox(
                hub,
                "CentralPlaza",
                new Vector3(0f, 0.015f, 10f),
                new Vector3(64f, 0.03f, 40f),
                palette.Road,
                false);

            CreateBox(hub, "WestServiceHall", new Vector3(-27f, 4f, 12f),
                new Vector3(10f, 8f, 32f), palette.Structure);
            CreateBox(hub, "EastServiceHall", new Vector3(27f, 4f, 12f),
                new Vector3(10f, 8f, 32f), palette.Structure);
            CreateBox(hub, "NorthwestHall", new Vector3(-18f, 4f, 28f),
                new Vector3(18f, 8f, 8f), palette.Structure);
            CreateBox(hub, "NortheastHall", new Vector3(18f, 4f, 28f),
                new Vector3(18f, 8f, 8f), palette.Structure);

            CreateBox(hub, "SouthwestBunker", new Vector3(-29f, 2.5f, -8f),
                new Vector3(6f, 5f, 8f), palette.Structure);
            CreateBox(hub, "SoutheastBunker", new Vector3(29f, 2.5f, -8f),
                new Vector3(6f, 5f, 8f), palette.Structure);
            CreateBox(hub, "NorthwestWatchPost", new Vector3(-29f, 5f, 31f),
                new Vector3(6f, 10f, 6f), palette.Structure);
            CreateBox(hub, "NortheastWatchPost", new Vector3(29f, 5f, 31f),
                new Vector3(6f, 10f, 6f), palette.Structure);

            CreateBox(hub, "WestEntryPillar", new Vector3(-8f, 2f, -8f),
                new Vector3(2f, 4f, 2f), palette.Cover);
            CreateBox(hub, "EastEntryPillar", new Vector3(8f, 2f, -8f),
                new Vector3(2f, 4f, 2f), palette.Cover);
            CreateBox(hub, "WestPlazaCoverSouth", new Vector3(-15f, 0.7f, 1f),
                new Vector3(5f, 1.4f, 1.5f), palette.Cover);
            CreateBox(hub, "EastPlazaCoverSouth", new Vector3(15f, 0.7f, 1f),
                new Vector3(5f, 1.4f, 1.5f), palette.Cover);
            CreateBox(hub, "WestPlazaCoverNorth", new Vector3(-15f, 0.7f, 18f),
                new Vector3(5f, 1.4f, 1.5f), palette.Cover);
            CreateBox(hub, "EastPlazaCoverNorth", new Vector3(15f, 0.7f, 18f),
                new Vector3(5f, 1.4f, 1.5f), palette.Cover);

            var guides = CreateGroup(hub, "NpcPlacementGuides");
            CreateMarker(guides, "BankServiceMarker", new Vector3(-8f, 4f),
                palette.ServiceMarker);
            CreateMarker(guides, "RecoveryServiceMarker", new Vector3(0f, 4f),
                palette.ServiceMarker);
            CreateMarker(guides, "InsuranceNpcMarker", new Vector3(8f, 4f),
                palette.ServiceMarker);
            CreateMarker(guides, "GuardGroupMarker", new Vector3(-16f, 12f),
                palette.ServiceMarker, new Vector3(7f, 0.04f, 4f));
        }

        private static void BuildNorthTraversal(
            Transform environment,
            EnvironmentPalette palette)
        {
            var traversal = CreateGroup(environment, "NorthTraversal");
            CreateBox(traversal, "WestApproachWall", new Vector3(-18f, 1.5f, 62f),
                new Vector3(28f, 3f, 2f), palette.Structure);
            CreateBox(traversal, "EastApproachWall", new Vector3(18f, 1.5f, 62f),
                new Vector3(28f, 3f, 2f), palette.Structure);

            CreateBox(traversal, "WestPlatform", new Vector3(-30f, 2f, 101f),
                new Vector3(24f, 4f, 24f), palette.Structure);
            CreateBox(
                traversal,
                "WestPlatformRamp",
                new Vector3(-30f, 2f, 82f),
                new Vector3(8f, 0.6f, 24f),
                palette.Cover,
                true,
                Quaternion.Euler(-10f, 0f, 0f));
            CreateBox(traversal, "EastPlatform", new Vector3(30f, 3f, 139f),
                new Vector3(24f, 6f, 24f), palette.Structure);
            CreateBox(
                traversal,
                "EastPlatformRamp",
                new Vector3(30f, 3f, 116f),
                new Vector3(8f, 0.6f, 34f),
                palette.Cover,
                true,
                Quaternion.Euler(-10f, 0f, 0f));
            CreateBox(traversal, "NorthObservationDeck", new Vector3(0f, 4f, 176f),
                new Vector3(22f, 8f, 22f), palette.Structure);
            CreateBox(traversal, "ObservationDeckBridge", new Vector3(0f, 4.4f, 153f),
                new Vector3(8f, 0.8f, 26f), palette.Cover);

            for (var index = 0; index < 5; index++)
            {
                var stepHeight = 0.4f * (index + 1);
                CreateBox(
                    traversal,
                    "ObservationStep" + (index + 1).ToString("00"),
                    new Vector3(0f, stepHeight * 0.5f, 126f + (index * 2f)),
                    new Vector3(7f, stepHeight, 2f),
                    palette.Cover);
            }

            var coverPositions = new[]
            {
                new Vector3(-10f, 0.75f, 75f),
                new Vector3(10f, 1.5f, 80f),
                new Vector3(-52f, 1.5f, 92f),
                new Vector3(51f, 0.75f, 100f),
                new Vector3(-8f, 1.5f, 108f),
                new Vector3(8f, 0.75f, 118f),
                new Vector3(-48f, 0.75f, 132f),
                new Vector3(50f, 1.5f, 154f),
                new Vector3(-26f, 1.5f, 168f),
                new Vector3(27f, 0.75f, 184f)
            };
            for (var index = 0; index < coverPositions.Length; index++)
            {
                var highCover = index % 3 == 1;
                CreateBox(
                    traversal,
                    "TraversalCover" + (index + 1).ToString("00"),
                    coverPositions[index],
                    highCover
                        ? new Vector3(3f, 3f, 3f)
                        : new Vector3(6f, 1.5f, 2f),
                    palette.Cover,
                    true,
                    Quaternion.Euler(0f, index % 2 == 0 ? 18f : -18f, 0f));
            }

            CreateBox(traversal, "WestCourseTower", new Vector3(-57f, 5f, 178f),
                new Vector3(10f, 10f, 10f), palette.Structure);
            CreateBox(traversal, "EastCourseTower", new Vector3(57f, 5f, 178f),
                new Vector3(10f, 10f, 10f), palette.Structure);
        }

        private static void BuildEastSightline(
            Transform environment,
            EnvironmentPalette palette)
        {
            var sightline = CreateGroup(environment, "EastSightline");
            CreateBox(sightline, "NorthLaneWall", new Vector3(152f, 1f, 31f),
                new Vector3(144f, 2f, 2f), palette.Structure);
            CreateBox(sightline, "SouthLaneWall", new Vector3(152f, 1f, -31f),
                new Vector3(144f, 2f, 2f), palette.Structure);
            CreateBox(sightline, "RangeBackstop", new Vector3(226f, 4f, 0f),
                new Vector3(4f, 8f, 44f), palette.Boundary);
            CreateBox(sightline, "RangeObservationPost", new Vector3(86f, 4f, 23f),
                new Vector3(10f, 8f, 10f), palette.Structure);

            for (var index = 0; index < 12; index++)
            {
                var x = 96f + (index * 10.5f);
                var north = index % 2 == 0;
                var tall = index % 4 == 3;
                CreateBox(
                    sightline,
                    "SightlineCover" + (index + 1).ToString("00"),
                    new Vector3(x, tall ? 1.5f : 0.75f, north ? 13f : -13f),
                    tall
                        ? new Vector3(3f, 3f, 3f)
                        : new Vector3(5f, 1.5f, 2f),
                    palette.Cover,
                    true,
                    Quaternion.Euler(0f, north ? -12f : 12f, 0f));
            }
        }

        private static void BuildSouthwestWilderness(
            Transform environment,
            EnvironmentPalette palette)
        {
            var wilderness = CreateGroup(environment, "SouthwestWilderness");
            var rocks = new[]
            {
                new RockDefinition(-218f, -207f, 12f, 7f, 9f, 18f),
                new RockDefinition(-205f, -216f, 7f, 4f, 6f, -27f),
                new RockDefinition(-174f, -220f, 16f, 8f, 7f, 8f),
                new RockDefinition(-145f, -218f, 8f, 5f, 11f, 31f),
                new RockDefinition(-116f, -209f, 13f, 6f, 8f, -12f),
                new RockDefinition(-216f, -178f, 9f, 5f, 14f, 25f),
                new RockDefinition(-211f, -143f, 14f, 8f, 8f, -18f),
                new RockDefinition(-201f, -113f, 8f, 5f, 12f, 10f),
                new RockDefinition(-174f, -116f, 10f, 6f, 7f, 42f),
                new RockDefinition(-119f, -117f, 15f, 7f, 10f, -33f),
                new RockDefinition(-108f, -145f, 8f, 4f, 13f, 20f),
                new RockDefinition(-111f, -178f, 12f, 7f, 7f, -9f),
                new RockDefinition(-232f, -160f, 7f, 4f, 8f, 37f),
                new RockDefinition(-160f, -233f, 9f, 5f, 9f, -21f),
                new RockDefinition(-98f, -198f, 6f, 4f, 10f, 16f),
                new RockDefinition(-193f, -98f, 11f, 6f, 6f, -38f),
                new RockDefinition(-133f, -102f, 7f, 4f, 9f, 28f),
                new RockDefinition(-226f, -125f, 6f, 4f, 7f, -14f)
            };

            for (var index = 0; index < rocks.Length; index++)
            {
                var rock = rocks[index];
                CreateBox(
                    wilderness,
                    "WildernessRock" + (index + 1).ToString("00"),
                    new Vector3(rock.X, rock.Height * 0.5f, rock.Z),
                    new Vector3(rock.Width, rock.Height, rock.Depth),
                    palette.Wilderness,
                    true,
                    Quaternion.Euler(0f, rock.Yaw, index % 2 == 0 ? 4f : -4f));
            }

            var guides = CreateGroup(wilderness, "NpcPlacementGuides");
            CreateBox(
                guides,
                "WolfSpawnAreaGuide",
                new Vector3(-160f, 0.0125f, -160f),
                new Vector3(60f, 0.025f, 60f),
                palette.ServiceMarker,
                false);
        }

        private static void BuildSoutheastExpansion(
            Transform environment,
            EnvironmentPalette palette)
        {
            var expansion = CreateGroup(environment, "SoutheastExpansion");
            CreateBox(
                expansion,
                "ExpansionPadGuide",
                new Vector3(152f, 0.0125f, -152f),
                new Vector3(144f, 0.025f, 144f),
                palette.Expansion,
                false);

            var corners = new[]
            {
                new Vector3(84f, 2f, -84f),
                new Vector3(220f, 2f, -84f),
                new Vector3(84f, 2f, -220f),
                new Vector3(220f, 2f, -220f)
            };
            for (var index = 0; index < corners.Length; index++)
            {
                CreateBox(
                    expansion,
                    "ExpansionBeacon" + (index + 1).ToString("00"),
                    corners[index],
                    new Vector3(2f, 4f, 2f),
                    palette.Expansion);
            }

            CreateBox(expansion, "ExpansionCargo01", new Vector3(101f, 1f, -101f),
                new Vector3(4f, 2f, 4f), palette.Cover);
            CreateBox(expansion, "ExpansionCargo02", new Vector3(107f, 1.5f, -101f),
                new Vector3(5f, 3f, 4f), palette.Cover);
            CreateBox(expansion, "ExpansionCargo03", new Vector3(104f, 1f, -107f),
                new Vector3(4f, 2f, 5f), palette.Cover);
            CreateBox(expansion, "ExpansionCargo04", new Vector3(111f, 0.75f, -108f),
                new Vector3(3f, 1.5f, 3f), palette.Cover);
        }

        private static void BuildRouteGuides(
            Transform environment,
            EnvironmentPalette palette)
        {
            var routes = CreateGroup(environment, "RouteGuides");
            CreateBox(routes, "CentralToNorthRoute", new Vector3(0f, 0.01f, 48f),
                new Vector3(12f, 0.02f, 32f), palette.Road, false);
            CreateBox(routes, "CentralToEastRoute", new Vector3(56f, 0.01f, 0f),
                new Vector3(48f, 0.02f, 12f), palette.Road, false);
            CreateBox(routes, "CentralToSouthwestRoute", new Vector3(-57f, 0.01f, -57f),
                new Vector3(12f, 0.02f, 96f), palette.Road, false,
                Quaternion.Euler(0f, -45f, 0f));
            CreateBox(routes, "CentralToSoutheastRoute", new Vector3(57f, 0.01f, -57f),
                new Vector3(12f, 0.02f, 96f), palette.Road, false,
                Quaternion.Euler(0f, 45f, 0f));
        }

        private static void CreateMarker(
            Transform parent,
            string name,
            Vector2 position,
            Material material,
            Vector3? scale = null)
        {
            CreateBox(
                parent,
                name,
                new Vector3(position.x, 0.02f, position.y),
                scale ?? new Vector3(4f, 0.04f, 4f),
                material,
                false);
        }

        private static Transform CreateGroup(Transform parent, string name)
        {
            var group = new GameObject(name)
            {
                isStatic = true
            };
            group.transform.SetParent(parent, false);
            return group.transform;
        }

        private static GameObject CreateBox(
            Transform parent,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Material material,
            bool collidable = true,
            Quaternion? localRotation = null)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.isStatic = true;
            box.transform.SetParent(parent, false);
            box.transform.localPosition = localPosition;
            box.transform.localRotation = localRotation ?? Quaternion.identity;
            box.transform.localScale = localScale;
            box.GetComponent<MeshRenderer>().sharedMaterial = material;

            if (!collidable)
            {
                UnityEngine.Object.DestroyImmediate(box.GetComponent<BoxCollider>());
            }

            return box;
        }

        private static void SetStaticRecursively(GameObject root)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                child.gameObject.isStatic = true;
            }
        }

        private static GameObject FindGameObject(Scene scene, string objectName)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var transforms = root.GetComponentsInChildren<Transform>(true);
                foreach (var transform in transforms)
                {
                    if (string.Equals(transform.name, objectName, StringComparison.Ordinal))
                    {
                        return transform.gameObject;
                    }
                }
            }

            return null;
        }

        private readonly struct EnvironmentPalette
        {
            public EnvironmentPalette(
                Material ground,
                Material road,
                Material structure,
                Material cover,
                Material wilderness,
                Material expansion,
                Material boundary,
                Material serviceMarker)
            {
                Ground = ground;
                Road = road;
                Structure = structure;
                Cover = cover;
                Wilderness = wilderness;
                Expansion = expansion;
                Boundary = boundary;
                ServiceMarker = serviceMarker;
            }

            public Material Ground { get; }

            public Material Road { get; }

            public Material Structure { get; }

            public Material Cover { get; }

            public Material Wilderness { get; }

            public Material Expansion { get; }

            public Material Boundary { get; }

            public Material ServiceMarker { get; }
        }

        private readonly struct RockDefinition
        {
            public RockDefinition(
                float x,
                float z,
                float width,
                float height,
                float depth,
                float yaw)
            {
                X = x;
                Z = z;
                Width = width;
                Height = height;
                Depth = depth;
                Yaw = yaw;
            }

            public float X { get; }

            public float Z { get; }

            public float Width { get; }

            public float Height { get; }

            public float Depth { get; }

            public float Yaw { get; }
        }
    }
}
