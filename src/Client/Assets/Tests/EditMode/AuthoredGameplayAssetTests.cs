using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.WorldActors;
using Mmorpg.Client.Worlds;
using Mmorpg.GameSimulation;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Editor.Actors;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class AuthoredGameplayAssetTests
    {
        private const string InputActionsPath = "Assets/Input/PlayerControls.inputactions";
        private const string LocalPlayerPrefabPath = "Assets/Prefabs/Player/LocalPlayer.prefab";
        private const string RemotePlayerPrefabPath = "Assets/Prefabs/Player/RemotePlayer.prefab";
        private const string DevelopmentWorld1ScenePath =
            "Assets/Scenes/DevelopmentWorld1.unity";
        private const string DevelopmentWorld2ScenePath =
            "Assets/Scenes/DevelopmentWorld2.unity";

        [Test]
        public void PlayerControlsDefinesRequiredGameplayActions()
        {
            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);

            Assert.That(actions, Is.Not.Null);
            Assert.That(actions.FindActionMap("Player", false), Is.Not.Null);
            foreach (var actionName in new[]
            {
                "Select",
                "ContextMenu",
                "PointerPosition",
                "CameraOrbit",
                "CameraOrbitDelta",
                "CameraRotate",
                "CameraZoom",
                "Cancel",
                "ToggleWorldDebug",
                "ToggleInventory",
                "ToggleCharacterInventory",
                "ToggleEquipmentInventory"
            })
            {
                Assert.That(actions.FindAction(actionName, false), Is.Not.Null, actionName);
            }

            // Click-to-move replaced keyboard movement, sprint, jump, aim, mouse look, the
            // debug cursor toggle, and the crosshair interact key.
            foreach (var removedName in new[]
            {
                "Move", "Sprint", "Jump", "Aim", "Look", "ToggleDebugCursor", "Interact"
            })
            {
                Assert.That(actions.FindAction(removedName, false), Is.Null, removedName);
            }

            Assert.That(actions.FindAction("Select").bindings.Select(binding => binding.path),
                Does.Contain("<Mouse>/leftButton"));
            Assert.That(actions.FindAction("ContextMenu").bindings.Select(binding => binding.path),
                Does.Contain("<Mouse>/rightButton"));
            Assert.That(actions.FindAction("PointerPosition").bindings.Select(binding => binding.path),
                Does.Contain("<Pointer>/position"));
            Assert.That(actions.FindAction("CameraOrbit").bindings.Select(binding => binding.path),
                Does.Contain("<Mouse>/middleButton"));
            Assert.That(actions.FindAction("CameraOrbitDelta").bindings.Select(binding => binding.path),
                Does.Contain("<Mouse>/delta"));
            var rotatePaths = actions.FindAction("CameraRotate").bindings.Select(binding => binding.path);
            Assert.That(rotatePaths, Does.Contain("<Keyboard>/upArrow"));
            Assert.That(rotatePaths, Does.Contain("<Keyboard>/downArrow"));
            Assert.That(rotatePaths, Does.Contain("<Keyboard>/leftArrow"));
            Assert.That(rotatePaths, Does.Contain("<Keyboard>/rightArrow"));
            Assert.That(actions.FindAction("CameraZoom").bindings.Select(binding => binding.path),
                Does.Contain("<Mouse>/scroll/y"));
            Assert.That(actions.FindAction("Cancel").bindings.Select(binding => binding.path),
                Does.Contain("<Keyboard>/escape"));
            Assert.That(actions.FindAction("ToggleWorldDebug").bindings.Select(binding => binding.path),
                Does.Contain("<Keyboard>/f2"));
            Assert.That(actions.FindAction("ToggleInventory").bindings.Select(binding => binding.path),
                Does.Contain("<Keyboard>/i"));
            Assert.That(actions.FindAction("ToggleCharacterInventory").bindings.Select(binding => binding.path),
                Does.Contain("<Keyboard>/b"));
            Assert.That(actions.FindAction("ToggleEquipmentInventory").bindings.Select(binding => binding.path),
                Does.Contain("<Keyboard>/c"));
        }

        [Test]
        public void ClientConfigReferencesBundledGameplayCatalog()
        {
            var config = ClientConfig.Load(out var handle);
            try
            {
                Assert.That(handle.IsValid(), Is.True);
                Assert.That(config.ItemGameplayCatalog, Is.Not.Null);
                Assert.That(config.ItemGameplayCatalog.text, Does.Contain("\"catalogId\""));
                Assert.That(config.ItemGameplayCatalog.text, Does.Contain("\"revision\""));
            }
            finally
            {
                ClientAssets.Release(handle);
            }
        }

        [Test]
        public void LocalPlayerPrefabOwnsColliderInputAndCamera()
        {
            var prefabRoot = PrefabUtility.LoadPrefabContents(LocalPlayerPrefabPath);

            try
            {
                var characterController = prefabRoot.GetComponent<CharacterController>();
                var characterBody = prefabRoot.GetComponent<CharacterBody>();
                var playerInput = prefabRoot.GetComponent<PlayerInput>();
                var localInput = prefabRoot.GetComponent<LocalPlayerInput>();
                var localController = prefabRoot.GetComponent<LocalPlayerController>();

                Assert.That(characterController, Is.Not.Null);
                Assert.That(characterBody, Is.Not.Null);
                Assert.That(characterController.minMoveDistance, Is.Zero);
                Assert.That(characterController.center.y,
                    Is.EqualTo((characterController.height * 0.5f) + characterController.skinWidth));
                var contactEnvelopeBottom = characterController.center.y
                    - (characterController.height * 0.5f)
                    - characterController.skinWidth;
                Assert.That(contactEnvelopeBottom, Is.Zero.Within(0.0001f));
                Assert.That(characterController.skinWidth, Is.EqualTo(0.035f).Within(0.0001f));
                Assert.That(playerInput, Is.Not.Null);
                Assert.That(playerInput.actions, Is.Not.Null);
                Assert.That(playerInput.actions.name, Is.EqualTo("PlayerControls"));
                Assert.That(playerInput.defaultActionMap, Is.EqualTo("Player"));
                Assert.That(localInput, Is.Not.Null);
                Assert.That(localController, Is.Not.Null);
                Assert.That(localController.CameraTarget, Is.Not.EqualTo(prefabRoot.transform));
                Assert.That(localController.CameraTarget.name, Is.EqualTo("CameraTarget"));

                var cameraController = localController.PlayerCamera;
                Assert.That(cameraController, Is.Not.Null);
                Assert.That(cameraController.name, Is.EqualTo("LocalPlayerCamera"));
                Assert.That(cameraController.transform.parent, Is.EqualTo(prefabRoot.transform));

                var playerCamera = cameraController.GetComponent<Camera>();
                Assert.That(playerCamera, Is.Not.Null);
                Assert.That(playerCamera.nearClipPlane, Is.EqualTo(0.05f));
                Assert.That(playerCamera.farClipPlane, Is.EqualTo(500f));
                Assert.That(playerInput.camera, Is.EqualTo(playerCamera));
                Assert.That(prefabRoot.GetComponentsInChildren<Camera>(true), Has.Length.EqualTo(1));
                Assert.That(prefabRoot.GetComponentsInChildren<AudioListener>(true), Has.Length.EqualTo(1));

                var cameraProperties = new SerializedObject(cameraController);
                Assert.That(cameraProperties.FindProperty("target").objectReferenceValue,
                    Is.EqualTo(localController.CameraTarget));
                Assert.That(cameraProperties.FindProperty("minimumDistance").floatValue,
                    Is.EqualTo(0.6f));
                Assert.That(cameraProperties.FindProperty("maximumDistance").floatValue,
                    Is.EqualTo(15f));
                Assert.That(cameraProperties.FindProperty("initialDistance").floatValue,
                    Is.EqualTo(12f));
                Assert.That(cameraProperties.FindProperty("minimumPitch").floatValue,
                    Is.EqualTo(10f));
                Assert.That(cameraProperties.FindProperty("maximumPitch").floatValue,
                    Is.EqualTo(80f));
                Assert.That(cameraProperties.FindProperty("initialPitch").floatValue,
                    Is.EqualTo(40f));
                Assert.That(cameraProperties.FindProperty("fieldOfView").floatValue,
                    Is.EqualTo(55f));
                Assert.That(cameraProperties.FindProperty("shoulderOffset"), Is.Null);
                Assert.That(cameraProperties.FindProperty("aimDistance"), Is.Null);

                var bodyProperties = new SerializedObject(characterBody);
                Assert.That(bodyProperties.FindProperty("controllerSkinWidthRatio").floatValue,
                    Is.EqualTo(0.1f));
                var playerVisual = prefabRoot.transform.Find("PlayerVisual");
                Assert.That(playerVisual, Is.Not.Null);
                Assert.That(playerVisual.parent, Is.EqualTo(prefabRoot.transform));
                Assert.That(playerVisual.localPosition.y, Is.Zero.Within(0.0001f));
                Assert.That(prefabRoot.transform.Find("GroundAnchor"), Is.Null);

                characterController.height = 3f;
                localController.RefreshCharacterDimensions();
                Assert.That(characterController.center.y,
                    Is.EqualTo(1.5f + characterController.skinWidth).Within(0.0001f));
                Assert.That(characterController.skinWidth,
                    Is.EqualTo(characterController.radius * 0.1f).Within(0.0001f));

                var colliders = prefabRoot.GetComponentsInChildren<Collider>(true);
                Assert.That(colliders, Has.Length.EqualTo(1));
                Assert.That(colliders[0], Is.SameAs(characterController));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        [Test]
        public void OrbitCameraClampsTiltAndZoomAndKeepsTurning()
        {
            var cameraObject = new GameObject("OrbitCameraTest", typeof(Camera));
            try
            {
                var orbit = cameraObject.AddComponent<OrbitCameraController>();
                orbit.ApplyInput(Vector2.zero, Vector2.zero, 0f, 0f);
                Assert.That(orbit.Pitch, Is.EqualTo(40f));
                Assert.That(orbit.ZoomDistance, Is.EqualTo(12f));

                // Dragging up tilts toward the horizon until the 10 degree limit.
                orbit.ApplyInput(new Vector2(0f, 1000f), Vector2.zero, 0f, 0f);
                Assert.That(orbit.Pitch, Is.EqualTo(10f));
                orbit.ApplyInput(Vector2.zero, new Vector2(0f, 1f), 0f, 10f);
                Assert.That(orbit.Pitch, Is.EqualTo(80f));

                // One notch zooms in by 15 percent; many notches stop at the limits.
                orbit.ApplyInput(Vector2.zero, Vector2.zero, 1f, 0f);
                Assert.That(orbit.ZoomDistance, Is.EqualTo(10.2f).Within(0.0001f));
                for (var notch = 0; notch < 40; notch++)
                {
                    orbit.ApplyInput(Vector2.zero, Vector2.zero, -1f, 0f);
                }

                Assert.That(orbit.ZoomDistance, Is.EqualTo(15f));
                for (var notch = 0; notch < 40; notch++)
                {
                    orbit.ApplyInput(Vector2.zero, Vector2.zero, 1f, 0f);
                }

                Assert.That(orbit.ZoomDistance, Is.EqualTo(0.6f));

                // Turning wraps around instead of clamping.
                orbit.ApplyInput(new Vector2(4 * 360f, 0f), Vector2.zero, 0f, 0f);
                Assert.That(orbit.Yaw, Is.EqualTo(0f).Within(0.001f));
                orbit.ApplyInput(Vector2.zero, new Vector2(-1f, 0f), 0f, 0.5f);
                Assert.That(orbit.Yaw, Is.EqualTo(60f).Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void ClosestZoomKeepsTheCameraClearOfThePlayerBody()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LocalPlayerPrefabPath);
            var instance = Object.Instantiate(prefab);
            var bakedMesh = new Mesh();

            try
            {
                var controller = instance.GetComponent<LocalPlayerController>();
                var cameraProperties = new SerializedObject(controller.PlayerCamera);
                var closestDistance = cameraProperties.FindProperty("minimumDistance").floatValue;
                var minimumPitch = cameraProperties.FindProperty("minimumPitch").floatValue;
                var maximumPitch = cameraProperties.FindProperty("maximumPitch").floatValue;
                var pivot = controller.CameraTarget.position;

                var bodyPoints = new List<Vector3>();
                foreach (var renderer in instance.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    renderer.BakeMesh(bakedMesh, true);
                    var toWorld = Matrix4x4.TRS(
                        renderer.transform.position,
                        renderer.transform.rotation,
                        Vector3.one);
                    bodyPoints.AddRange(bakedMesh.vertices.Select(
                        vertex => toWorld.MultiplyPoint3x4(vertex)));
                }

                Assert.That(bodyPoints, Is.Not.Empty);

                // The closest zoom stays about 30 centimeters from the body at every tilt
                // and all around the player; the bound leaves room for the pose.
                for (var pitch = minimumPitch; pitch <= maximumPitch; pitch += 5f)
                {
                    for (var yaw = 0f; yaw < 360f; yaw += 15f)
                    {
                        var direction = Quaternion.Euler(pitch, yaw, 0f) * Vector3.back;
                        var cameraPosition = pivot + (direction * closestDistance);
                        var clearance = bodyPoints.Min(
                            point => Vector3.Distance(point, cameraPosition));
                        Assert.That(clearance, Is.GreaterThan(0.2f), $"pitch {pitch}, yaw {yaw}");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(bakedMesh);
                Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void LocalPlayerPrefabBindsActionsWhenInstantiated()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LocalPlayerPrefabPath);
            var instance = Object.Instantiate(prefab);

            try
            {
                var controller = instance.GetComponent<LocalPlayerController>();
                Assert.That(instance.GetComponent<LocalPlayerInput>().Initialize(), Is.True);
                Assert.That(controller.enabled, Is.True);

                var spawnPosition = new Vector3(2f, 3f, 4f);
                controller.Teleport(spawnPosition, Quaternion.Euler(0f, 90f, 0f));
                Assert.That(Vector3.Distance(instance.transform.position, spawnPosition),
                    Is.LessThan(0.0001f));
                Assert.That(Vector3.Distance(
                    instance.GetComponent<CharacterBody>().GroundPosition,
                    spawnPosition), Is.LessThan(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void RemotePlayerPrefabIsPresentationOnly()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RemotePlayerPrefabPath);

            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.GetComponent<RemotePlayerView>(), Is.Not.Null);
            Assert.That(prefab.GetComponentInChildren<Renderer>(true), Is.Not.Null);
            Assert.That(prefab.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Camera>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<AudioListener>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<PlayerInput>(true), Is.Empty);
        }

        [TestCase(DevelopmentWorld1ScenePath, "development-world-1")]
        [TestCase(DevelopmentWorld2ScenePath, "development-world-2")]
        public void DevelopmentWorldSceneUsesTheAuthoredWorldComposition(string scenePath, string worldId)
        {
            var manifest = DevelopmentWorldBuilder.LoadManifest(worldId);
            WithScene(scenePath, scene =>
            {
                var environment = FindGameObject(scene, "Environment");
                var gameplay = FindGameObject(scene, "Gameplay");
                var spawnPoint = FindGameObject(scene, "PlayerSpawn");
                var context = FindGameObject(scene, "WorldSceneContext");
                var entityPresentationRoot = FindGameObject(scene, "EntityPresentationRoot");

                Assert.That(environment, Is.Not.Null);
                Assert.That(gameplay, Is.Not.Null);
                Assert.That(spawnPoint, Is.Not.Null);
                Assert.That(context, Is.Not.Null);
                Assert.That(entityPresentationRoot, Is.Not.Null);
                Assert.That(FindGameObject(scene, "Main Camera"), Is.Null);
                Assert.That(FindGameObject(scene, "LocalPlayer"), Is.Null);
                Assert.That(spawnPoint.transform.parent, Is.EqualTo(gameplay.transform));
                Assert.That(entityPresentationRoot.transform.parent, Is.EqualTo(gameplay.transform));

                // PlayerSpawn follows the World manifest, which the worker uses.
                Assert.That(
                    spawnPoint.transform.localPosition,
                    Is.EqualTo(new Vector3(manifest.spawn.x, manifest.spawn.y, manifest.spawn.z)));
                Assert.That(spawnPoint.transform.localEulerAngles.y, Is.EqualTo(manifest.spawn.yawDegrees).Within(0.001f));

                var contextProperties = new SerializedObject(context.GetComponent<WorldSceneContext>());
                var localPlayerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LocalPlayerPrefabPath);
                Assert.That(contextProperties.FindProperty("localPlayerPrefab").objectReferenceValue,
                    Is.EqualTo(localPlayerPrefab.GetComponent<LocalPlayerController>()));
                Assert.That(contextProperties.FindProperty("playerSpawnPoint").objectReferenceValue,
                    Is.EqualTo(spawnPoint.transform));
                var remotePlayerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RemotePlayerPrefabPath);
                Assert.That(contextProperties.FindProperty("remotePlayerPrefab").objectReferenceValue,
                    Is.EqualTo(remotePlayerPrefab.GetComponent<RemotePlayerView>()));
                Assert.That(contextProperties.FindProperty("entityPresentationRoot").objectReferenceValue,
                    Is.EqualTo(entityPresentationRoot.transform));

                var authoring = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<WorldCollisionAuthoring>(true))
                    .Single();
                Assert.That(authoring.WorldId, Is.EqualTo(worldId));
                Assert.That(authoring.ChunkSize, Is.EqualTo(DevelopmentWorldBuilder.ChunkSize));
                Assert.That(authoring.CollisionRoot, Is.EqualTo(environment.transform));
                Assert.That(authoring.LayerMask, Is.EqualTo((uint)DevelopmentWorldBuilder.CollisionLayerMask));

                // Only bakeable colliders, nothing dynamic, and everything static.
                var colliders = environment.GetComponentsInChildren<Collider>(true);
                Assert.That(colliders.All(collider => !collider.isTrigger), Is.True);
                Assert.That(
                    colliders.All(collider => collider is BoxCollider
                        || collider is TerrainCollider
                        || (collider is MeshCollider mesh && !mesh.convex)),
                    Is.True);
                Assert.That(environment.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
                Assert.That(environment.GetComponentsInChildren<Transform>(true)
                    .All(transform => transform.gameObject.isStatic), Is.True);

                // Labels are depth-tested world-space text that never takes pointer input.
                Assert.That(
                    environment.GetComponentsInChildren<Canvas>(true).All(canvas => canvas.renderMode == RenderMode.WorldSpace),
                    Is.True);
                Assert.That(environment.GetComponentsInChildren<UnityEngine.UI.GraphicRaycaster>(true), Is.Empty);
                Assert.That(
                    environment.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).All(graphic => !graphic.raycastTarget),
                    Is.True);

                // The boundary walls' inner faces lie on the manifest bounds.
                var bounds = manifest.bounds;
                AssertInnerFace(scene, "NorthWall", wall => wall.min.z, bounds.maximumZ);
                AssertInnerFace(scene, "SouthWall", wall => wall.max.z, bounds.minimumZ);
                AssertInnerFace(scene, "EastWall", wall => wall.min.x, bounds.maximumX);
                AssertInnerFace(scene, "WestWall", wall => wall.max.x, bounds.minimumX);
                var ground = FindGameObject(scene, "Ground").GetComponent<BoxCollider>().bounds;
                Assert.That(ground.max.y, Is.EqualTo(manifest.groundHeight).Within(0.0001f));
                Assert.That(ground.min.x, Is.LessThanOrEqualTo(bounds.minimumX - 1f));
                Assert.That(ground.max.x, Is.GreaterThanOrEqualTo(bounds.maximumX + 1f));
            });
        }

        [Test]
        public void DevelopmentWorldOneIsTheStressArena()
        {
            WithScene(DevelopmentWorld1ScenePath, scene =>
            {
                foreach (var group in new[] { "Spawn", "Obstacles", "ServiceBooth", "GatheringField", "WolfPen", "Platform" })
                {
                    Assert.That(FindGameObject(scene, group), Is.Not.Null, group);
                }

                var environment = FindGameObject(scene, "Environment");
                Assert.That(environment.GetComponentsInChildren<TerrainCollider>(true), Is.Empty);
                Assert.That(environment.GetComponentsInChildren<MeshCollider>(true), Is.Empty);
            });
        }

        [Test]
        public void DevelopmentWorldTwoHasEveryTestZone()
        {
            WithScene(DevelopmentWorld2ScenePath, scene =>
            {
                foreach (var zone in new[]
                         {
                             "Town", "Roads", "MovementCourse", "Highlands", "CombatRange", "InterestRoad",
                             "WolfWoods", "Quarry", "Meadow", "Sandbox"
                         })
                {
                    Assert.That(FindGameObject(scene, zone), Is.Not.Null, zone);
                }

                var environment = FindGameObject(scene, "Environment");
                var tunnel = environment.GetComponentsInChildren<MeshCollider>(true).Single();
                Assert.That(tunnel.convex, Is.False);
                var terrain = environment.GetComponentsInChildren<TerrainCollider>(true).Single();
                Assert.That(terrain.transform.position, Is.EqualTo(DevelopmentWorld2Builder.HighlandsOrigin));
                Assert.That(terrain.terrainData.size, Is.EqualTo(DevelopmentWorld2Builder.HighlandsSize));
            });
        }
        [Test]
        public void ProjectRendersWithUrpOnEveryQualityLevel()
        {
            Assert.That(GraphicsSettings.defaultRenderPipeline, Is.Not.Null);
            Assert.That(
                GraphicsSettings.defaultRenderPipeline.GetType().Name,
                Is.EqualTo("UniversalRenderPipelineAsset"));
            for (var level = 0; level < QualitySettings.names.Length; level++)
            {
                var levelPipeline = QualitySettings.GetRenderPipelineAssetAt(level);
                Assert.That(levelPipeline, Is.Not.Null, QualitySettings.names[level]);
                Assert.That(
                    levelPipeline.GetType().Name,
                    Is.EqualTo("UniversalRenderPipelineAsset"),
                    QualitySettings.names[level]);
            }
        }

        [Test]
        public void EveryAuthoredActorArchetypeHasAPresentationPrefab()
        {
            var registry = AssetDatabase.LoadAssetAtPath<WorldActorPresentationRegistry>(
                ClientAddresses.ContentRoot + ClientAddresses.WorldActorPresentationRegistry);
            Assert.That(registry, Is.Not.Null);
            var workspace = new WorldActorEditorService(WorldActorEditorPaths.CreateDefault()).Load();
            var compiled = WorldActorCompiler.Compile(
                workspace.Catalog.ToDomain(),
                workspace.Spawns.ToDomain());

            Assert.That(compiled.Actors, Is.Not.Empty);
            foreach (var actor in compiled.Actors)
            {
                Assert.That(
                    registry.TryResolve(actor.PresentationArchetypeId, out var reference),
                    Is.True,
                    actor.PresentationArchetypeId);
                var prefab = reference.editorAsset;
                Assert.That(prefab, Is.Not.Null, actor.PresentationArchetypeId);
                // The prefab folder is an Addressables folder entry, so the reference loads.
                Assert.That(
                    AssetDatabase.GetAssetPath(prefab),
                    Does.StartWith(ClientAddressablesLayout.WorldActorPrefabFolder + "/"),
                    actor.PresentationArchetypeId);
                // Presentation never takes part in physics or pointer raycasts.
                Assert.That(
                    prefab.GetComponentsInChildren<Collider>(true),
                    Is.Empty,
                    actor.PresentationArchetypeId);
            }
        }

        [Test]
        public void EveryResourceNodePrefabHasAvailableAndDepletedModels()
        {
            var registry = AssetDatabase.LoadAssetAtPath<WorldActorPresentationRegistry>(
                ClientAddresses.ContentRoot + ClientAddresses.WorldActorPresentationRegistry);
            var workspace = new WorldActorEditorService(WorldActorEditorPaths.CreateDefault()).Load();
            var resourceNodes = WorldActorCompiler.Compile(
                    workspace.Catalog.ToDomain(),
                    workspace.Spawns.ToDomain())
                .Actors
                .Where(actor => actor.Kind == WorldActorKindIds.ResourceNode)
                .ToArray();

            Assert.That(resourceNodes, Is.Not.Empty);
            foreach (var actor in resourceNodes)
            {
                Assert.That(registry.TryResolve(actor.PresentationArchetypeId, out var reference), Is.True);
                var prefab = reference.editorAsset;
                // ResourceNodeView switches these direct children when the node depletes.
                foreach (var childName in new[]
                         {
                             ResourceNodeView.AvailableModelName,
                             ResourceNodeView.DepletedModelName
                         })
                {
                    var child = prefab.transform.Find(childName);
                    Assert.That(child, Is.Not.Null, actor.PresentationArchetypeId + " " + childName);
                    Assert.That(
                        child.GetComponentsInChildren<Renderer>(true),
                        Is.Not.Empty,
                        actor.PresentationArchetypeId + " " + childName);
                }
            }
        }

        [Test]
        public void IronVeinPresentationIsAVariantOfTheExportedModel()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                ClientAddressablesLayout.WorldActorPrefabFolder + "/Resource_IronVein.prefab");

            Assert.That(prefab, Is.Not.Null);
            Assert.That(PrefabUtility.GetPrefabAssetType(prefab), Is.EqualTo(PrefabAssetType.Variant));
            Assert.That(
                AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromOriginalSource(prefab)),
                Is.EqualTo(ArtModelImportRules.ModelFolder + "/WorldActors/Resource_IronVein.fbx"));
        }

        // URP Lit everywhere, except the body materials that paint layers paint over, which use
        // the skin shader, URP Lit with paint layers.
        [Test]
        public void EveryProjectMaterialUsesTheUrpShaderOfItsKind()
        {
            // URP Lit, or its copies for painted skin and for garments.
            var painted = ArtAssetManifests.PaintedMaterialNames();
            var garments = ArtAssetManifests.GarmentMaterialNames();
            var materialGuids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
            Assert.That(materialGuids, Is.Not.Empty);
            foreach (var materialGuid in materialGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(materialGuid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                Assert.That(material, Is.Not.Null, path);
                Assert.That(material.shader, Is.Not.Null, path);
                Assert.That(
                    material.shader.name,
                    Is.EqualTo(garments.Contains(material.name)
                        ? ArtMaterialCreator.GarmentShader
                        : painted.Contains(material.name) ? ArtMaterialCreator.SkinShader : ArtMaterialCreator.LitShader),
                    path);
            }
        }

        // The builders create the shared palette, so a material that no development World
        // uses any more is stale.
        [Test]
        public void EveryDevelopmentWorldMaterialIsUsedByADevelopmentWorld()
        {
            var used = AssetDatabase.GetDependencies(
                new[] { DevelopmentWorld1ScenePath, DevelopmentWorld2ScenePath },
                true);
            var materials = AssetDatabase.FindAssets("t:Material", new[] { DevelopmentWorldPalette.MaterialDirectory })
                .Select(AssetDatabase.GUIDToAssetPath)
                .ToArray();

            Assert.That(materials, Is.Not.Empty);
            foreach (var material in materials)
            {
                Assert.That(used, Does.Contain(material));
            }
        }

        // The checked-in bake must be current: it holds exactly the scene's boxes, terrain
        // cells, and mesh triangles, in every chunk the ground covers.
        [TestCase(DevelopmentWorld1ScenePath, "development-world-1")]
        [TestCase(DevelopmentWorld2ScenePath, "development-world-2")]
        public void BakedWorldCollisionMatchesItsScene(string scenePath, string worldId)
        {
            Assert.That(
                UnityWorldCollisionLoader.TryLoad(worldId, out var collisionWorld, out var error),
                Is.True,
                error);
            WithScene(scenePath, scene =>
            {
                var environment = FindGameObject(scene, "Environment");
                var ground = FindGameObject(scene, "Ground").GetComponent<BoxCollider>().bounds;
                var chunkSize = DevelopmentWorldBuilder.ChunkSize;
                var chunksX = Mathf.FloorToInt((ground.max.x - 0.001f) / chunkSize) - Mathf.FloorToInt(ground.min.x / chunkSize) + 1;
                var chunksZ = Mathf.FloorToInt((ground.max.z - 0.001f) / chunkSize) - Mathf.FloorToInt(ground.min.z / chunkSize) + 1;
                Assert.That(collisionWorld.ChunkCount, Is.EqualTo(chunksX * chunksZ));

                var expectedTriangles = environment.GetComponentsInChildren<MeshCollider>(true)
                    .Sum(mesh => mesh.sharedMesh.triangles.Length / 3);
                foreach (var terrain in environment.GetComponentsInChildren<TerrainCollider>(true))
                {
                    var data = terrain.terrainData;
                    var cells = data.heightmapResolution - 1;
                    var holes = data.GetHoles(0, 0, cells, cells);
                    var solidCells = holes.Cast<bool>().Count(solid => solid);
                    expectedTriangles += solidCells * 2;
                }

                var buffer = new CollisionQueryBuffer();
                collisionWorld.QueryShapes(
                    new CollisionAabb(
                        new SimulationVector3(ground.min.x, -2f, ground.min.z),
                        new SimulationVector3(ground.max.x, 64f, ground.max.z)),
                    CollisionLayers.CharacterMovement,
                    buffer);
                Assert.That(
                    buffer.Shapes.OfType<CollisionBox>().Count(),
                    Is.EqualTo(environment.GetComponentsInChildren<BoxCollider>(true).Length));
                Assert.That(buffer.Shapes.OfType<CollisionTriangle>().Count(), Is.EqualTo(expectedTriangles));
            });
        }
        // Guards the terrain bake mapping: the baked surface must match the Unity Terrain at
        // every heightmap sample away from the tunnel portals.
        [Test]
        public void BakedHighlandsCollisionMatchesTheTerrainAtItsSamples()
        {
            var scene = SceneManager.GetSceneByPath(DevelopmentWorld2ScenePath);
            var openedForTest = !scene.IsValid() || !scene.isLoaded;
            if (openedForTest)
            {
                scene = EditorSceneManager.OpenScene(
                    DevelopmentWorld2ScenePath,
                    OpenSceneMode.Additive);
            }

            try
            {
                var terrain = FindGameObject(scene, "HighlandsTerrain").GetComponent<Terrain>();
                Assert.That(UnityWorldCollisionLoader.TryLoad(
                    "development-world-2",
                    out var collisionWorld,
                    out var error), Is.True, error);
                var buffer = new CollisionQueryBuffer();
                var origin = terrain.transform.position;
                for (var z = 0; z <= 128; z += 7)
                {
                    for (var x = 0; x <= 128; x += 5)
                    {
                        if (x >= 45 && x <= 55)
                        {
                            continue;
                        }

                        var point = new Vector3(origin.x + x, 0f, origin.z + z);
                        var expected = origin.y + terrain.SampleHeight(point);
                        Assert.That(CollisionRaycast.TryRaycast(
                            collisionWorld,
                            new SimulationVector3(point.x, 100f, point.z),
                            new SimulationVector3(0f, -1f, 0f),
                            200f,
                            CollisionLayers.CharacterMovement,
                            buffer,
                            out var hit), Is.True, point.ToString());
                        Assert.That(hit.Point.Y, Is.EqualTo(expected).Within(0.001f), point.ToString());
                    }
                }
            }
            finally
            {
                if (openedForTest)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static void WithScene(string scenePath, System.Action<Scene> assertions)
        {
            var scene = SceneManager.GetSceneByPath(scenePath);
            var openedForTest = !scene.IsValid() || !scene.isLoaded;
            if (openedForTest)
            {
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }

            try
            {
                assertions(scene);
            }
            finally
            {
                if (openedForTest)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static void AssertInnerFace(
            Scene scene,
            string wallName,
            System.Func<Bounds, float> innerFace,
            float expected)
        {
            var wall = FindGameObject(scene, wallName);
            Assert.That(wall, Is.Not.Null, wallName + " is missing.");
            Assert.That(innerFace(wall.GetComponent<BoxCollider>().bounds), Is.EqualTo(expected).Within(0.001f), wallName);
        }
        private static GameObject FindGameObject(Scene scene, string objectName)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var match = FindInHierarchy(root.transform, objectName);
                if (match != null)
                {
                    return match.gameObject;
                }
            }

            return null;
        }

        private static Transform FindInHierarchy(Transform current, string objectName)
        {
            if (current.name == objectName)
            {
                return current;
            }

            for (var childIndex = 0; childIndex < current.childCount; childIndex++)
            {
                var match = FindInHierarchy(current.GetChild(childIndex), objectName);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
