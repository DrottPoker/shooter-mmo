using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Gameplay
{
    // A copy of the local character on a stage far below the world, dressed as the character is and
    // standing in its idle, which a camera of its own draws into a texture for the Equipment window.
    // The player cameras end long before the stage and the stage camera long before the world, so
    // neither sees the other, and the stage's lights reach only the copy. The camera draws only
    // while a window shows the texture; then the copy animates and its cloth swings, and otherwise
    // it costs nothing. The window turns the copy around its height.
    [DisallowMultipleComponent]
    public sealed class CharacterPreview : MonoBehaviour
    {
        public const string StageName = "CharacterPreviewStage";
        public const string BodyName = "Body";

        // Far below every World and beyond every player camera's far plane.
        public static readonly Vector3 StagePosition = new Vector3(0f, -10000f, 0f);

        // Twice the size the texture shows at on a 1080p screen, so the figure stays sharp.
        public const int TextureWidth = 320;
        public const int TextureHeight = 640;

        // The camera looks level at a body standing at the stage's origin and frames 2.2 m of
        // height, with the feet a little above the bottom, where the window's pedestal is.
        public const float CameraDistance = 4.4f;
        public const float CameraHeight = 0.9f;
        public const float FieldOfView = 28f;
        public const float CameraFarPlane = 12f;

        private Transform pivot;
        private Camera stageCamera;
        private RenderTexture texture;
        private PlayerBodyAppearance body;
        private string shownModel;
        private float yaw;

        // The picture of the copy, for a raw image to show, or null without a graphics device.
        public RenderTexture Texture
        {
            get { return texture; }
        }

        public Camera Camera
        {
            get { return stageCamera; }
        }

        // The copy of the character, or null before Show.
        public PlayerBodyAppearance Body
        {
            get { return body; }
        }

        // Whether the camera draws the copy, as while the Equipment window is open; never without
        // a texture to draw into.
        public bool Visible
        {
            get { return stageCamera != null && stageCamera.enabled; }
            set
            {
                var drawn = value && texture != null;
                if (stageCamera != null && stageCamera.enabled != drawn)
                {
                    stageCamera.enabled = drawn;
                }
            }
        }

        // How far the copy is turned from facing the camera, in degrees.
        public float Yaw
        {
            get { return yaw; }
            set
            {
                yaw = Mathf.Repeat(value, 360f);
                if (pivot != null)
                {
                    pivot.localRotation = Quaternion.Euler(0f, yaw, 0f);
                }
            }
        }

        public static CharacterPreview Create(Transform parent)
        {
            var stage = new GameObject(StageName);
            stage.transform.SetParent(parent, false);
            stage.transform.SetPositionAndRotation(StagePosition, Quaternion.identity);
            var preview = stage.AddComponent<CharacterPreview>();
            preview.Build();
            return preview;
        }

        // Shows the body of the model in the look, putting in a new copy when the model changed.
        public void Show(PlayerBodyModels.Model model, BodyLook look, PlayerBodyMeshes meshes, PlayerBodyMaterials materials)
        {
            if (model == null || model.Body == null || pivot == null)
            {
                return;
            }

            if (body == null || !string.Equals(shownModel, model.Name, System.StringComparison.Ordinal))
            {
                RemoveBody();
                body = Instantiate(model.Body, pivot, false);
                body.name = BodyName;
                shownModel = model.Name;

                // No player camera ever sees the copy, so it animates only while the stage camera
                // draws it.
                var animator = body.GetComponent<Animator>();
                if (animator != null)
                {
                    animator.cullingMode = AnimatorCullingMode.CullCompletely;
                }

                body.CombineParts(meshes);
            }

            body.ApplyLook(look ?? BodyLook.Default, materials);
        }

        private void Build()
        {
            pivot = new GameObject("Pivot").transform;
            pivot.SetParent(transform, false);
            Yaw = 0f;

            // Without a graphics device, as in the headless test run, a render texture cannot be
            // made; the stage then shows its copy to nobody.
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            {
                texture = new RenderTexture(TextureWidth, TextureHeight, 24, RenderTextureFormat.ARGB32)
                {
                    name = "CharacterPreview",
                    antiAliasing = 4,
                    useMipMap = false
                };
                texture.Create();
            }

            var cameraObject = new GameObject("Camera");
            cameraObject.transform.SetParent(transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, CameraHeight, CameraDistance);
            cameraObject.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            stageCamera = cameraObject.AddComponent<Camera>();
            stageCamera.clearFlags = CameraClearFlags.SolidColor;
            stageCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            stageCamera.fieldOfView = FieldOfView;
            stageCamera.nearClipPlane = 0.1f;
            stageCamera.farClipPlane = CameraFarPlane;
            stageCamera.allowHDR = false;
            stageCamera.allowMSAA = true;
            stageCamera.targetTexture = texture;
            stageCamera.enabled = false;

            // A warm key from the front left, a cool fill from the front right, and a rim from
            // behind; their short range keeps them on the stage.
            AddLight("KeyLight", new Vector3(-1.6f, 2.4f, 2.6f), new Color(1f, 0.93f, 0.82f), 2.2f);
            AddLight("FillLight", new Vector3(1.8f, 1.4f, 2.2f), new Color(0.75f, 0.85f, 1f), 1.0f);
            AddLight("RimLight", new Vector3(0.4f, 2.2f, -2.2f), new Color(0.75f, 0.9f, 1f), 1.6f);
            AddClothView();
        }

        private void AddLight(string lightName, Vector3 position, Color color, float intensity)
        {
            var lightObject = new GameObject(lightName);
            lightObject.transform.SetParent(transform, false);
            lightObject.transform.localPosition = position;
            var stageLight = lightObject.AddComponent<Light>();
            stageLight.type = LightType.Point;
            stageLight.color = color;
            stageLight.intensity = intensity;
            stageLight.range = 6f;
            stageLight.shadows = LightShadows.None;
        }

        private void RemoveBody()
        {
            if (body == null)
            {
                return;
            }

            var old = body.gameObject;
            body = null;
            shownModel = null;
            // Destroy waits for the end of the frame; until then the old copy must not draw.
            old.SetActive(false);
            old.transform.SetParent(null, false);
            if (Application.isPlaying)
            {
                Destroy(old);
            }
            else
            {
                DestroyImmediate(old);
            }
        }

        // The cloth of the copy swings while the stage camera draws it.
        private void OnEnable()
        {
            AddClothView();
        }

        private void AddClothView()
        {
            if (Application.isPlaying && isActiveAndEnabled && stageCamera != null)
            {
                ClothChainScheduler.AddView(stageCamera);
            }
        }

        private void OnDisable()
        {
            ClothChainScheduler.RemoveView(stageCamera);
        }

        private void OnDestroy()
        {
            if (texture == null)
            {
                return;
            }

            if (stageCamera != null)
            {
                stageCamera.targetTexture = null;
            }

            texture.Release();
            if (Application.isPlaying)
            {
                Destroy(texture);
            }
            else
            {
                DestroyImmediate(texture);
            }

            texture = null;
        }
    }
}
