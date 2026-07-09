using UnityEngine;
using UnityEngine.InputSystem;

namespace ShooterMmo.Gameplay
{
    public sealed class ThirdPersonCameraController : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private float targetHeight = 1.45f;
        [SerializeField] private float distance = 6f;
        [SerializeField] private float minDistance = 3f;
        [SerializeField] private float maxDistance = 10f;
        [SerializeField] private float mouseSensitivity = 0.12f;
        [SerializeField] private float zoomSensitivity = 0.03f;
        [SerializeField] private float minPitch = -15f;
        [SerializeField] private float maxPitch = 55f;

        private float yaw;
        private float pitch = 20f;
        private bool ownsCursorLock;

        private void LateUpdate()
        {
            if (target == null)
            {
                ReleaseCursorLock();
                return;
            }

            ReadCameraInput();
            ApplyCameraTransform();
        }

        private void OnDisable()
        {
            ReleaseCursorLock();
        }

        public void SetTarget(Transform followTarget)
        {
            target = followTarget;
            if (followTarget != null)
            {
                yaw = followTarget.eulerAngles.y;
            }
        }

        private void ReadCameraInput()
        {
            var mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            if (mouse.rightButton.isPressed)
            {
                ownsCursorLock = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;

                var delta = mouse.delta.ReadValue();
                yaw += delta.x * mouseSensitivity;
                pitch = Mathf.Clamp(pitch - (delta.y * mouseSensitivity), minPitch, maxPitch);
            }
            else
            {
                ReleaseCursorLock();
            }

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                distance = Mathf.Clamp(distance - (scroll * zoomSensitivity), minDistance, maxDistance);
            }
        }

        private void ApplyCameraTransform()
        {
            var focusPosition = target.position + (Vector3.up * targetHeight);
            var cameraRotation = Quaternion.Euler(pitch, yaw, 0f);
            var cameraPosition = focusPosition + (cameraRotation * new Vector3(0f, 0f, -distance));
            transform.SetPositionAndRotation(
                cameraPosition,
                Quaternion.LookRotation(focusPosition - cameraPosition, Vector3.up));
        }

        private void ReleaseCursorLock()
        {
            if (!ownsCursorLock)
            {
                return;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            ownsCursorLock = false;
        }
    }
}

