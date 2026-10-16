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
        private const string DevelopmentWorld2MaterialDirectory =
            "Assets/Art/Materials/DevelopmentWorld2";
        private const int DevelopmentWorld2CollisionBoxCount = 112;

        // 128 by 128 terrain cells less 60 portal holes, two triangles each, plus the 14
        // quads of the tunnel arch.
        private const int DevelopmentWorld2CollisionTriangleCount = ((128 * 128) - 60) * 2 + 28;

        [Test]
        public void PlayerControlsDefinesRequiredGameplayActions()
        {
            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);

            Assert.That(actions, Is.Not.Null);
            Assert.That(actions.FindActionMap("Player", false), Is.Not.Null);
            foreach (var actionName in new[]
            {
                "Select",
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
                    Is.EqualTo(3f));
                Assert.That(cameraProperties.FindProperty("maximumDistance").floatValue,
                    Is.EqualTo(30f));
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

                Assert.That(orbit.ZoomDistance, Is.EqualTo(30f));
                for (var notch = 0; notch < 40; notch++)
                {
                    orbit.ApplyInput(Vector2.zero, Vector2.zero, 1f, 0f);
                }

                Assert.That(orbit.ZoomDistance, Is.EqualTo(3f));

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

        [Test]
        public void DevelopmentWorldOneUsesAuthoredGameplayComposition()
        {
            var scene = SceneManager.GetSceneByPath(DevelopmentWorld1ScenePath);
            var openedForTest = !scene.IsValid() || !scene.isLoaded;

            if (openedForTest)
            {
                scene = EditorSceneManager.OpenScene(
                    DevelopmentWorld1ScenePath,
                    OpenSceneMode.Additive);
            }

            try
            {
                var environment = FindGameObject(scene, "Environment");
                var gameplay = FindGameObject(scene, "Gameplay");
                var spawnPoint = FindGameObject(scene, "PlayerSpawn");
                var localPlayer = FindGameObject(scene, "LocalPlayer");
                var context = FindGameObject(scene, "WorldSceneContext");
                var entityPresentationRoot = FindGameObject(scene, "EntityPresentationRoot");

                Assert.That(environment, Is.Not.Null);
                Assert.That(gameplay, Is.Not.Null);
                Assert.That(spawnPoint, Is.Not.Null);
                Assert.That(context, Is.Not.Null);
                Assert.That(entityPresentationRoot, Is.Not.Null);
                Assert.That(FindGameObject(scene, "Main Camera"), Is.Null);
                Assert.That(spawnPoint.transform.parent, Is.EqualTo(gameplay.transform));
                Assert.That(spawnPoint.transform.localPosition, Is.EqualTo(new Vector3(0f, 0f, -1f)));
                Assert.That(localPlayer, Is.Null);
                Assert.That(entityPresentationRoot.transform.parent, Is.EqualTo(gameplay.transform));

                var sceneContext = context.GetComponent<WorldSceneContext>();
                var contextProperties = new SerializedObject(sceneContext);
                var localPlayerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LocalPlayerPrefabPath);
                Assert.That(contextProperties.FindProperty("localPlayerPrefab").objectReferenceValue,
                    Is.EqualTo(localPlayerPrefab.GetComponent<LocalPlayerController>()));
                Assert.That(contextProperties.FindProperty("playerCamera"), Is.Null);
                Assert.That(contextProperties.FindProperty("playerSpawnPoint").objectReferenceValue,
                    Is.EqualTo(spawnPoint.transform));
                var remotePlayerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RemotePlayerPrefabPath);
                Assert.That(contextProperties.FindProperty("remotePlayerPrefab").objectReferenceValue,
                    Is.EqualTo(remotePlayerPrefab.GetComponent<RemotePlayerView>()));
                Assert.That(contextProperties.FindProperty("entityPresentationRoot").objectReferenceValue,
                    Is.EqualTo(entityPresentationRoot.transform));

                Assert.That(environment.GetComponentsInChildren<BoxCollider>(true), Has.Length.EqualTo(13));
                Assert.That(environment.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
                Assert.That(environment.GetComponentsInChildren<Transform>(true)
                    .All(transform => transform.gameObject.isStatic), Is.True);

                Assert.That(FindGameObject(scene, "NorthWall"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "Ramp"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "CameraTestWall"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "Step01"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "Step02"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "Step03"), Is.Not.Null);
            }
            finally
            {
                if (openedForTest)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        [Test]
        public void DevelopmentWorldTwoMatchesItsAuthoredWorldContract()
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
                var environment = FindGameObject(scene, "Environment");
                var gameplay = FindGameObject(scene, "Gameplay");
                var spawnPoint = FindGameObject(scene, "PlayerSpawn");
                var authoring = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<WorldCollisionAuthoring>(true))
                    .Single();

                Assert.That(environment, Is.Not.Null);
                Assert.That(gameplay, Is.Not.Null);
                Assert.That(spawnPoint, Is.Not.Null);
                Assert.That(spawnPoint.transform.parent, Is.EqualTo(gameplay.transform));
                Assert.That(
                    spawnPoint.transform.localPosition,
                    Is.EqualTo(new Vector3(0f, 0f, -16f)));
                Assert.That(authoring.WorldId, Is.EqualTo("development-world-2"));
                Assert.That(authoring.ChunkSize, Is.EqualTo(32f));
                Assert.That(authoring.CollisionRoot, Is.EqualTo(environment.transform));

                AssertTransform(scene, "Ground", new Vector3(0f, -0.25f, 0f),
                    new Vector3(512f, 0.5f, 512f));
                AssertTransform(scene, "NorthWall", new Vector3(0f, 3f, 255f),
                    new Vector3(512f, 6f, 2f));
                AssertTransform(scene, "SouthWall", new Vector3(0f, 3f, -255f),
                    new Vector3(512f, 6f, 2f));
                AssertTransform(scene, "EastWall", new Vector3(255f, 3f, 0f),
                    new Vector3(2f, 6f, 512f));
                AssertTransform(scene, "WestWall", new Vector3(-255f, 3f, 0f),
                    new Vector3(2f, 6f, 512f));

                Assert.That(FindGameObject(scene, "CentralHub"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "NorthTraversal"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "EastSightline"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "SouthwestWilderness"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "SoutheastExpansion"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "NpcPlacementGuides"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "BankServiceMarker"), Is.Not.Null);
                Assert.That(FindGameObject(scene, "WolfSpawnAreaGuide"), Is.Not.Null);
                var colliders = environment.GetComponentsInChildren<Collider>(true);
                Assert.That(
                    colliders.OfType<BoxCollider>().ToArray(),
                    Has.Length.EqualTo(DevelopmentWorld2CollisionBoxCount));
                var tunnel = colliders.OfType<MeshCollider>().Single();
                Assert.That(tunnel.convex, Is.False);
                var terrain = colliders.OfType<TerrainCollider>().Single();
                Assert.That(terrain.transform.position, Is.EqualTo(new Vector3(96f, 0f, 96f)));
                Assert.That(terrain.terrainData.size, Is.EqualTo(new Vector3(128f, 40f, 128f)));
                Assert.That(colliders, Has.Length.EqualTo(DevelopmentWorld2CollisionBoxCount + 2));
                Assert.That(colliders.All(collider => !collider.isTrigger), Is.True);
                Assert.That(environment.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
                Assert.That(environment.GetComponentsInChildren<Transform>(true)
                    .All(transform => transform.gameObject.isStatic), Is.True);

                AssertClearOfEnvironmentObstacles(colliders, new Vector3(0f, 1f, -16f));
                AssertClearOfEnvironmentObstacles(colliders, new Vector3(-8f, 1f, 4f));
                AssertClearOfEnvironmentObstacles(colliders, new Vector3(0f, 1f, 4f));
                AssertClearOfEnvironmentObstacles(colliders, new Vector3(8f, 1f, 4f));
                AssertClearOfEnvironmentObstacles(colliders, new Vector3(-16f, 1f, 12f));
                AssertClearOfEnvironmentObstacles(colliders, new Vector3(-184f, 1f, -184f));
                AssertClearOfEnvironmentObstacles(colliders, new Vector3(-136f, 1f, -176f));
                AssertClearOfEnvironmentObstacles(colliders, new Vector3(-148f, 1f, -136f));
            }
            finally
            {
                if (openedForTest)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
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
        public void EveryProjectMaterialUsesTheUrpLitShader()
        {
            Assert.That(
                AssetDatabase.FindAssets("t:Material", new[] { DevelopmentWorld2MaterialDirectory }),
                Has.Length.EqualTo(8));
            var materialGuids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
            Assert.That(materialGuids, Is.Not.Empty);
            foreach (var materialGuid in materialGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(materialGuid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                Assert.That(material, Is.Not.Null, path);
                Assert.That(material.shader, Is.Not.Null, path);
                Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"), path);
            }
        }

        [Test]
        public void BakedWorldCollisionLoadsAllAuthoredBoxes()
        {
            var loaded = UnityWorldCollisionLoader.TryLoad(
                "development-world-1",
                out var collisionWorld,
                out var error);

            Assert.That(loaded, Is.True, error);
            Assert.That(collisionWorld.ChunkCount, Is.EqualTo(4));
            Assert.That(collisionWorld.Revision, Is.Not.Empty);
            var buffer = new CollisionQueryBuffer();
            collisionWorld.QueryShapes(
                new CollisionAabb(
                    new SimulationVector3(-16f, -2f, -16f),
                    new SimulationVector3(16f, 5f, 16f)),
                CollisionLayers.CharacterMovement,
                buffer);
            Assert.That(buffer.Shapes, Has.Count.EqualTo(13));
        }

        [Test]
        public void BakedDevelopmentWorldTwoCollisionLoadsAllAuthoredShapes()
        {
            var loaded = UnityWorldCollisionLoader.TryLoad(
                "development-world-2",
                out var collisionWorld,
                out var error);

            Assert.That(loaded, Is.True, error);
            Assert.That(collisionWorld.ChunkCount, Is.EqualTo(256));
            Assert.That(collisionWorld.Revision, Is.Not.Empty);
            var buffer = new CollisionQueryBuffer();
            collisionWorld.QueryShapes(
                new CollisionAabb(
                    new SimulationVector3(-256f, -2f, -256f),
                    new SimulationVector3(256f, 64f, 256f)),
                CollisionLayers.CharacterMovement,
                buffer);
            Assert.That(
                buffer.Shapes.OfType<CollisionBox>().Count(),
                Is.EqualTo(DevelopmentWorld2CollisionBoxCount));
            Assert.That(
                buffer.Shapes.OfType<CollisionTriangle>().Count(),
                Is.EqualTo(DevelopmentWorld2CollisionTriangleCount));
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

        private static void AssertClearOfEnvironmentObstacles(
            Collider[] colliders,
            Vector3 point)
        {
            foreach (var collider in colliders)
            {
                if (collider.name == "Ground")
                {
                    continue;
                }

                // ClosestPoint supports only primitive and convex colliders, so terrains
                // and meshes must keep the position outside their bounds instead.
                if (collider is TerrainCollider || (collider is MeshCollider mesh && !mesh.convex))
                {
                    Assert.That(
                        collider.bounds.Contains(point),
                        Is.False,
                        "Protected gameplay position " + point + " lies within the bounds of "
                        + collider.name + ".");
                    continue;
                }

                var closestPoint = collider.ClosestPoint(point);
                Assert.That(
                    (closestPoint - point).sqrMagnitude,
                    Is.GreaterThan(0.0001f),
                    "Protected gameplay position " + point + " overlaps "
                    + collider.name + ".");
            }
        }

        private static void AssertTransform(
            Scene scene,
            string objectName,
            Vector3 expectedPosition,
            Vector3 expectedScale)
        {
            var gameObject = FindGameObject(scene, objectName);
            Assert.That(gameObject, Is.Not.Null, objectName + " is missing.");
            Assert.That(gameObject.transform.localPosition, Is.EqualTo(expectedPosition));
            Assert.That(gameObject.transform.localScale, Is.EqualTo(expectedScale));
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
