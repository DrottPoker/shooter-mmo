using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The look of a player body. Its visible skinned parts are drawn as one combined renderer,
    // and its skin materials take a tint: a faint multiply of the skin's painted color, from
    // shared tinted materials, that leaves the eyes, teeth, and tongue as they are. No tint
    // restores the body's own materials.
    [DisallowMultipleComponent]
    public sealed class PlayerBodyAppearance : MonoBehaviour
    {
        // How far the skin moves toward the tint color.
        public const float TintStrength = 0.35f;

        public const string CombinedRendererName = "CombinedBody";

        [SerializeField] private Material[] skinMaterials = new Material[0];

        private readonly Dictionary<Renderer, Material[]> ownMaterials = new Dictionary<Renderer, Material[]>();
        private SkinnedMeshRenderer combinedRenderer;

        public IReadOnlyList<Material> SkinMaterials
        {
            get { return skinMaterials; }
        }

        // The renderer that draws the combined parts, or null before CombineParts.
        public SkinnedMeshRenderer CombinedRenderer
        {
            get { return combinedRenderer; }
        }

        // Draws the visible parts as one skinned renderer with the shared mesh of these parts,
        // and hides the parts. Equipment that hides a zone combines the body again.
        public void CombineParts(PlayerBodyMeshes meshes)
        {
            if (meshes == null || combinedRenderer != null)
            {
                return;
            }

            RestoreOwnMaterials();
            var parts = GetComponentsInChildren<SkinnedMeshRenderer>(false)
                .Where(part => part.enabled && part.sharedMesh != null)
                .ToArray();
            if (parts.Length == 0)
            {
                return;
            }

            var combined = meshes.For(parts, transform);
            var transforms = new Dictionary<string, Transform>();
            foreach (var child in GetComponentsInChildren<Transform>(true))
            {
                transforms[child.name] = child;
            }

            var rendererObject = new GameObject(CombinedRendererName);
            rendererObject.transform.SetParent(transform, false);
            combinedRenderer = rendererObject.AddComponent<SkinnedMeshRenderer>();
            combinedRenderer.sharedMesh = combined.Mesh;
            combinedRenderer.bones = combined.BoneNames.Select(boneName => transforms[boneName]).ToArray();
            combinedRenderer.rootBone = transforms.TryGetValue(combined.RootBoneName, out var rootBone)
                ? rootBone
                : transform;
            combinedRenderer.localBounds = combined.LocalBounds;
            combinedRenderer.sharedMaterials = combined.Materials.ToArray();
            combinedRenderer.shadowCastingMode = parts[0].shadowCastingMode;
            combinedRenderer.receiveShadows = parts[0].receiveShadows;
            combinedRenderer.quality = parts[0].quality;
            combinedRenderer.updateWhenOffscreen = false;
            foreach (var part in parts)
            {
                part.enabled = false;
            }

            ownMaterials.Clear();

            // The Animator culls by its renderers, which changed.
            var animator = GetComponent<Animator>();
            if (animator != null)
            {
                animator.Rebind();
            }
        }

        public static Color TintFor(Color color)
        {
            var tint = Color.Lerp(Color.white, color, TintStrength);
            tint.a = 1f;
            return tint;
        }

        // Tints the skin toward the color, or restores the body's own materials when tinted
        // is false.
        public void ApplyTint(bool tinted, Color color, PlayerBodyTintMaterials materials)
        {
            CaptureOwnMaterials();
            var tint = TintFor(color);
            foreach (var entry in ownMaterials)
            {
                if (entry.Key == null)
                {
                    continue;
                }

                if (!tinted || materials == null)
                {
                    entry.Key.sharedMaterials = entry.Value;
                    continue;
                }

                var applied = (Material[])entry.Value.Clone();
                for (var index = 0; index < applied.Length; index++)
                {
                    if (applied[index] != null && System.Array.IndexOf(skinMaterials, applied[index]) >= 0)
                    {
                        applied[index] = materials.For(applied[index], tint);
                    }
                }

                entry.Key.sharedMaterials = applied;
            }
        }

        private void CaptureOwnMaterials()
        {
            if (ownMaterials.Count > 0)
            {
                return;
            }

            foreach (var bodyRenderer in GetComponentsInChildren<Renderer>(true))
            {
                ownMaterials[bodyRenderer] = bodyRenderer.sharedMaterials;
            }
        }

        private void RestoreOwnMaterials()
        {
            foreach (var entry in ownMaterials)
            {
                if (entry.Key != null)
                {
                    entry.Key.sharedMaterials = entry.Value;
                }
            }
        }
    }
}
