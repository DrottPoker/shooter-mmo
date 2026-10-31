using Mmorpg.Client.Core;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Replaceable presentation: a flat disc at the end of the path the local player
    // follows. It hides when the predicted state reaches the end or the player stands.
    [DisallowMultipleComponent]
    public sealed class MovementDestinationMarker : MonoBehaviour
    {
        private const float MarkerDiameter = 0.6f;
        private const float MarkerThickness = 0.02f;
        private const float GroundOffset = 0.03f;
        private static readonly Color MarkerColor = new Color(1f, 0.85f, 0.2f, 1f);

        private LocalPlayerController localPlayer;
        private Material primitiveMaterial;
        private GameObject marker;
        private Material markerMaterial;

        // Called by the composition root before the component is activated.
        public void Initialize(Material runtimePrimitiveMaterial)
        {
            primitiveMaterial = runtimePrimitiveMaterial;
        }

        private void LateUpdate()
        {
            if (localPlayer == null)
            {
                localPlayer = FindAnyObjectByType<LocalPlayerController>();
            }

            var path = localPlayer != null && localPlayer.IsServerAuthoritative
                ? localPlayer.CurrentPath
                : null;
            if (path == null || !localPlayer.PredictedState.PathProgress.IsFollowing(path))
            {
                SetVisible(false);
                return;
            }

            EnsureMarker();
            var destination = path.Destination;
            marker.transform.position = new Vector3(
                destination.X,
                destination.Y + GroundOffset,
                destination.Z);
            SetVisible(true);
        }

        private void OnDestroy()
        {
            if (marker != null)
            {
                Destroy(marker);
            }

            if (markerMaterial != null)
            {
                Destroy(markerMaterial);
            }
        }

        private void EnsureMarker()
        {
            if (marker != null)
            {
                return;
            }

            marker = RuntimePrimitives.Create(PrimitiveType.Cylinder, primitiveMaterial);
            marker.name = "MovementDestinationMarker";
            marker.transform.SetParent(transform, false);
            marker.transform.localScale = new Vector3(MarkerDiameter, MarkerThickness, MarkerDiameter);
            // The marker must never block clicks or the camera, so its collider goes at once.
            var markerCollider = marker.GetComponent<Collider>();
            if (markerCollider != null)
            {
                DestroyImmediate(markerCollider);
            }

            var markerRenderer = marker.GetComponent<Renderer>();
            markerMaterial = new Material(markerRenderer.sharedMaterial);
            markerMaterial.SetColor("_BaseColor", MarkerColor);
            markerMaterial.SetColor("_Color", MarkerColor);
            markerRenderer.sharedMaterial = markerMaterial;
            markerRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            markerRenderer.receiveShadows = false;
        }

        private void SetVisible(bool isVisible)
        {
            if (marker != null && marker.activeSelf != isVisible)
            {
                marker.SetActive(isVisible);
            }
        }
    }
}
