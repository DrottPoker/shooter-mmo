using UnityEngine;

namespace ShooterMmo.Gameplay
{
    public sealed class WorldSceneGameplayBootstrap : MonoBehaviour
    {
        private const string EnvironmentName = "WorldSceneRuntimeEnvironment";
        private const string PlayerName = "LocalPlayer";
        private const string RuntimeCameraName = "WorldSceneCamera";

        private static readonly Vector3 SafeCitySpawn = Vector3.zero;

        private void Start()
        {
            EnsureEnvironment();
            var player = EnsureLocalPlayer();
            var camera = EnsureCamera(player.CameraTarget);
            player.SetCameraTransform(camera.transform);
        }

        private static void EnsureEnvironment()
        {
            if (GameObject.Find(EnvironmentName) != null)
            {
                return;
            }

            var root = new GameObject(EnvironmentName);

            var groundMaterial = CreateRuntimeMaterial("Open Risk Ground", new Color(0.22f, 0.32f, 0.24f));
            var plazaMaterial = CreateRuntimeMaterial("Safe City Stone", new Color(0.45f, 0.46f, 0.42f));
            var wallMaterial = CreateRuntimeMaterial("Safe City Wall", new Color(0.28f, 0.31f, 0.34f));
            var gateMaterial = CreateRuntimeMaterial("Risk Boundary", new Color(0.68f, 0.23f, 0.18f));
            var coverMaterial = CreateRuntimeMaterial("Outside Cover", new Color(0.32f, 0.25f, 0.19f));

            CreatePrimitive(root.transform, PrimitiveType.Plane, "OpenRiskGround", new Vector3(0f, -0.02f, 0f), new Vector3(14f, 1f, 14f), groundMaterial);
            CreatePrimitive(root.transform, PrimitiveType.Cube, "SafeCityPlaza", new Vector3(0f, -0.01f, 0f), new Vector3(18f, 0.02f, 18f), plazaMaterial);

            CreatePrimitive(root.transform, PrimitiveType.Cube, "SafeCityBackWall", new Vector3(0f, 0.6f, -9.5f), new Vector3(18.5f, 1.2f, 0.4f), wallMaterial);
            CreatePrimitive(root.transform, PrimitiveType.Cube, "SafeCityLeftWall", new Vector3(-9.5f, 0.6f, 0f), new Vector3(0.4f, 1.2f, 18.5f), wallMaterial);
            CreatePrimitive(root.transform, PrimitiveType.Cube, "SafeCityRightWall", new Vector3(9.5f, 0.6f, 0f), new Vector3(0.4f, 1.2f, 18.5f), wallMaterial);
            CreatePrimitive(root.transform, PrimitiveType.Cube, "SafeCityFrontWallLeft", new Vector3(-5.75f, 0.6f, 9.5f), new Vector3(7.5f, 1.2f, 0.4f), wallMaterial);
            CreatePrimitive(root.transform, PrimitiveType.Cube, "SafeCityFrontWallRight", new Vector3(5.75f, 0.6f, 9.5f), new Vector3(7.5f, 1.2f, 0.4f), wallMaterial);

            CreatePrimitive(root.transform, PrimitiveType.Cube, "OpenRiskBoundaryMarker", new Vector3(0f, 0.03f, 14f), new Vector3(13f, 0.04f, 0.35f), gateMaterial);
            CreatePrimitive(root.transform, PrimitiveType.Cube, "OutsideCoverA", new Vector3(-5f, 0.5f, 20f), new Vector3(2f, 1f, 2f), coverMaterial);
            CreatePrimitive(root.transform, PrimitiveType.Cube, "OutsideCoverB", new Vector3(4f, 0.5f, 23f), new Vector3(3f, 1f, 1.5f), coverMaterial);

            EnsureDirectionalLight();
        }

        private static LocalPlayerController EnsureLocalPlayer()
        {
            var existingPlayer = GameObject.Find(PlayerName);
            if (existingPlayer != null)
            {
                var existingController = existingPlayer.GetComponent<LocalPlayerController>();
                if (existingController != null)
                {
                    EnsureCameraTarget(existingController);
                    return existingController;
                }
            }

            var player = new GameObject(PlayerName);
            player.transform.position = SafeCitySpawn;

            var characterController = player.AddComponent<CharacterController>();
            characterController.center = new Vector3(0f, 1f, 0f);
            characterController.height = 2f;
            characterController.radius = 0.35f;
            characterController.stepOffset = 0.35f;
            characterController.slopeLimit = 45f;

            var controller = player.AddComponent<LocalPlayerController>();

            var playerMaterial = CreateRuntimeMaterial("Local Player", new Color(0.18f, 0.47f, 0.82f));
            var weaponMaterial = CreateRuntimeMaterial("Starter Weapon", new Color(0.08f, 0.08f, 0.09f));

            var visual = CreatePrimitive(player.transform, PrimitiveType.Capsule, "PlayerVisual", new Vector3(0f, 1f, 0f), new Vector3(0.75f, 1f, 0.75f), playerMaterial);
            DestroyPrimitiveCollider(visual);

            var weapon = CreatePrimitive(player.transform, PrimitiveType.Cube, "StarterWeaponVisual", new Vector3(0.42f, 1.15f, 0.45f), new Vector3(0.16f, 0.16f, 0.9f), weaponMaterial);
            DestroyPrimitiveCollider(weapon);

            EnsureCameraTarget(controller);
            return controller;
        }

        private static Camera EnsureCamera(Transform target)
        {
            var sceneCamera = Camera.main;
            if (sceneCamera == null)
            {
                sceneCamera = FindFirstObjectByType<Camera>();
            }

            if (sceneCamera == null)
            {
                var cameraObject = new GameObject(RuntimeCameraName);
                sceneCamera = cameraObject.AddComponent<Camera>();
                sceneCamera.tag = "MainCamera";
            }

            sceneCamera.nearClipPlane = 0.05f;
            sceneCamera.farClipPlane = 500f;

            var cameraController = sceneCamera.GetComponent<ThirdPersonCameraController>();
            if (cameraController == null)
            {
                cameraController = sceneCamera.gameObject.AddComponent<ThirdPersonCameraController>();
            }

            cameraController.SetTarget(target);
            return sceneCamera;
        }

        private static void EnsureCameraTarget(LocalPlayerController controller)
        {
            if (controller.CameraTarget != controller.transform)
            {
                return;
            }

            var cameraTarget = new GameObject("CameraTarget");
            cameraTarget.transform.SetParent(controller.transform, false);
            cameraTarget.transform.localPosition = new Vector3(0f, 1.45f, 0f);
            controller.SetCameraTarget(cameraTarget.transform);
        }

        private static void EnsureDirectionalLight()
        {
            if (FindFirstObjectByType<Light>() != null)
            {
                return;
            }

            var lightObject = new GameObject("WorldSceneDirectionalLight");
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
        }

        private static GameObject CreatePrimitive(Transform parent, PrimitiveType primitiveType, string objectName, Vector3 localPosition, Vector3 localScale, Material material)
        {
            var primitive = GameObject.CreatePrimitive(primitiveType);
            primitive.name = objectName;
            primitive.transform.SetParent(parent, false);
            primitive.transform.localPosition = localPosition;
            primitive.transform.localScale = localScale;

            var renderer = primitive.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
            }

            return primitive;
        }

        private static Material CreateRuntimeMaterial(string materialName, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            var material = new Material(shader)
            {
                name = materialName,
                color = color
            };

            return material;
        }

        private static void DestroyPrimitiveCollider(GameObject primitive)
        {
            var collider = primitive.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
        }
    }
}

