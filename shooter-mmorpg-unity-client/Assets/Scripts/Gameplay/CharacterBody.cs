using UnityEngine;

namespace ShooterMmo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public sealed class CharacterBody : MonoBehaviour
    {
        private const float MinimumControllerSkinWidth = 0.01f;

        [SerializeField, Range(0.05f, 0.2f)] private float controllerSkinWidthRatio = 0.1f;

        private CharacterController characterController;

        public CharacterController Controller
        {
            get
            {
                EnsureConfigured();
                return characterController;
            }
        }

        public Vector3 GroundPosition
        {
            get { return transform.position; }
        }

        private void Awake()
        {
            EnsureConfigured();
        }

        private void OnValidate()
        {
            EnsureConfigured();
        }

        public void Teleport(Vector3 groundPosition, Quaternion rotation)
        {
            EnsureConfigured();

            var wasEnabled = characterController.enabled;
            characterController.enabled = false;
            transform.SetPositionAndRotation(groundPosition, rotation);
            characterController.enabled = wasEnabled;
        }

        public void RefreshDimensions()
        {
            EnsureConfigured();
        }

        private void EnsureConfigured()
        {
            if (characterController == null)
            {
                characterController = GetComponent<CharacterController>();
            }

            if (characterController == null)
            {
                return;
            }

            characterController.skinWidth = Mathf.Max(
                MinimumControllerSkinWidth,
                characterController.radius * controllerSkinWidthRatio);

            var center = characterController.center;
            center.y = (characterController.height * 0.5f) + characterController.skinWidth;
            characterController.center = center;
        }
    }
}
