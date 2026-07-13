using System.Linq;
using NUnit.Framework;
using ShooterMmo.Collision;
using ShooterMmo.GameSimulation;
using ShooterMmo.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class AuthoredGameplayAssetTests
    {
        private const string InputActionsPath = "Assets/Input/PlayerControls.inputactions";
        private const string LocalPlayerPrefabPath = "Assets/Prefabs/Player/LocalPlayer.prefab";
        private const string RemotePlayerPrefabPath = "Assets/Prefabs/Player/RemotePlayer.prefab";
        private const string WorldScenePath = "Assets/Scenes/WorldScene.unity";

        [Test]
        public void PlayerControlsDefinesRequiredGameplayActions()
        {
            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);

            Assert.That(actions, Is.Not.Null);
            Assert.That(actions.FindActionMap("Player", false), Is.Not.Null);
            Assert.That(actions.FindAction("Move", false), Is.Not.Null);
            Assert.That(actions.FindAction("Sprint", false), Is.Not.Null);
            Assert.That(actions.FindAction("Jump", false), Is.Not.Null);
            Assert.That(actions.FindAction("Aim", false), Is.Not.Null);
            Assert.That(actions.FindAction("Look", false), Is.Not.Null);
            Assert.That(actions.FindAction("ToggleDebugCursor", false), Is.Not.Null);
            Assert.That(actions.FindAction("ToggleWorldDebug", false), Is.Not.Null);
            Assert.That(actions.FindAction("Orbit", false), Is.Null);
            Assert.That(actions.FindAction("Zoom", false), Is.Null);

            var movePaths = actions.FindAction("Move").bindings.Select(binding => binding.path);
            Assert.That(movePaths, Does.Contain("<Keyboard>/w"));
            Assert.That(movePaths, Does.Contain("<Keyboard>/a"));
            Assert.That(movePaths, Does.Contain("<Keyboard>/s"));
            Assert.That(movePaths, Does.Contain("<Keyboard>/d"));
            Assert.That(actions.FindAction("Aim").bindings.Select(binding => binding.path),
                Does.Contain("<Mouse>/rightButton"));
            Assert.That(actions.FindAction("Look").bindings.Select(binding => binding.path),
                Does.Contain("<Mouse>/delta"));
            Assert.That(actions.FindAction("ToggleDebugCursor").bindings.Select(binding => binding.path),
                Does.Contain("<Keyboard>/f1"));
            Assert.That(actions.FindAction("ToggleWorldDebug").bindings.Select(binding => binding.path),
                Does.Contain("<Keyboard>/f2"));
        }

        [Test]
        public void DynamicCrosshairSupportsWeaponDefinitionsAndSpread()
        {
            var gameObject = new GameObject("CrosshairTest");

            try
            {
                var controller = gameObject.AddComponent<CrosshairController>();
                Assert.That(controller.CurrentDefinition.Shape, Is.EqualTo(CrosshairShape.Dot));
                Assert.That(controller.CurrentDefinition.Size, Is.EqualTo(4f));
                Assert.That(controller.CurrentDefinition.Gap, Is.Zero);

                var weaponCrosshair = new CrosshairDefinition(
                    CrosshairShape.Cross,
                    12f,
                    2f,
                    4f,
                    Color.red);
                controller.SetCrosshair(weaponCrosshair);
                controller.SetDynamicSpread(6f);

                Assert.That(controller.CurrentDefinition.Shape, Is.EqualTo(CrosshairShape.Cross));
                Assert.That(controller.CurrentDefinition.Size, Is.EqualTo(12f));
                Assert.That(controller.CurrentDefinition.Color, Is.EqualTo(Color.red));
                Assert.That(controller.DynamicSpread, Is.EqualTo(6f));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
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
                Assert.That(cameraProperties.FindProperty("shoulderOffset").floatValue,
                    Is.EqualTo(1.1f));
                Assert.That(cameraProperties.FindProperty("aimShoulderOffset").floatValue,
                    Is.EqualTo(1.3f));
                Assert.That(cameraProperties.FindProperty("verticalOffset").floatValue,
                    Is.EqualTo(0.45f));
                Assert.That(cameraProperties.FindProperty("aimVerticalOffset").floatValue,
                    Is.EqualTo(0.35f));
                Assert.That(cameraProperties.FindProperty("distance").floatValue,
                    Is.EqualTo(4.75f));
                Assert.That(cameraProperties.FindProperty("aimDistance").floatValue,
                    Is.EqualTo(4.25f));
                Assert.That(cameraProperties.FindProperty("initialPitch").floatValue,
                    Is.EqualTo(12f));
                Assert.That(cameraProperties.FindProperty("minPitch").floatValue,
                    Is.EqualTo(-50f));
                Assert.That(cameraProperties.FindProperty("maxPitch").floatValue,
                    Is.EqualTo(75f));
                Assert.That(cameraProperties.FindProperty("normalFieldOfView").floatValue,
                    Is.EqualTo(60f));
                Assert.That(cameraProperties.FindProperty("aimFieldOfView").floatValue,
                    Is.EqualTo(45f));

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
        public void WorldSceneUsesAuthoredGameplayComposition()
        {
            var scene = SceneManager.GetSceneByPath(WorldScenePath);
            var openedForTest = !scene.IsValid() || !scene.isLoaded;

            if (openedForTest)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            try
            {
                var environment = FindGameObject(scene, "Environment");
                var gameplay = FindGameObject(scene, "Gameplay");
                var spawnPoint = FindGameObject(scene, "PlayerSpawn");
                var localPlayer = FindGameObject(scene, "LocalPlayer");
                var context = FindGameObject(scene, "WorldSceneContext");
                var localPlayerCamera = FindGameObject(scene, "LocalPlayerCamera");

                Assert.That(environment, Is.Not.Null);
                Assert.That(gameplay, Is.Not.Null);
                Assert.That(spawnPoint, Is.Not.Null);
                Assert.That(context, Is.Not.Null);
                Assert.That(FindGameObject(scene, "Main Camera"), Is.Null);
                Assert.That(spawnPoint.transform.parent, Is.EqualTo(gameplay.transform));
                Assert.That(spawnPoint.transform.localPosition, Is.EqualTo(new Vector3(0f, 0f, -1f)));
                Assert.That(localPlayer, Is.Not.Null);
                Assert.That(localPlayer.transform.parent, Is.EqualTo(gameplay.transform));
                Assert.That(PrefabUtility.IsPartOfPrefabInstance(localPlayer), Is.True);
                Assert.That(localPlayerCamera, Is.Not.Null);
                Assert.That(localPlayerCamera.transform.IsChildOf(localPlayer.transform), Is.True);
                Assert.That(localPlayer.GetComponent<LocalPlayerController>().PlayerCamera,
                    Is.EqualTo(localPlayerCamera.GetComponent<ThirdPersonCameraController>()));

                var sceneContext = context.GetComponent<WorldSceneContext>();
                var contextProperties = new SerializedObject(sceneContext);
                Assert.That(contextProperties.FindProperty("localPlayer").objectReferenceValue,
                    Is.EqualTo(localPlayer.GetComponent<LocalPlayerController>()));
                Assert.That(contextProperties.FindProperty("playerCamera"), Is.Null);
                Assert.That(contextProperties.FindProperty("playerSpawnPoint").objectReferenceValue,
                    Is.EqualTo(spawnPoint.transform));
                var remotePlayerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RemotePlayerPrefabPath);
                Assert.That(contextProperties.FindProperty("remotePlayerPrefab").objectReferenceValue,
                    Is.EqualTo(remotePlayerPrefab.GetComponent<RemotePlayerView>()));

                Assert.That(environment.GetComponentsInChildren<BoxCollider>(true), Has.Length.EqualTo(12));
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
        public void BakedWorldCollisionLoadsAllAuthoredBoxes()
        {
            var loaded = UnityWorldCollisionLoader.TryLoad(
                "local-world-1",
                out var collisionWorld,
                out var error);

            Assert.That(loaded, Is.True, error);
            Assert.That(collisionWorld.ChunkCount, Is.EqualTo(4));
            Assert.That(collisionWorld.Revision, Is.Not.Empty);
            var buffer = new CollisionQueryBuffer();
            collisionWorld.QueryBoxes(
                new CollisionAabb(
                    new SimulationVector3(-16f, -2f, -16f),
                    new SimulationVector3(16f, 5f, 16f)),
                CollisionLayers.CharacterMovement,
                buffer);
            Assert.That(buffer.Boxes, Has.Count.EqualTo(12));
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
