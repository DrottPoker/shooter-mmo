using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The look of a player body. Its visible skinned parts are drawn as one combined renderer.
    // A look from the body's appearance catalog shows other images on its painted materials,
    // such as another face or eye color, and a tint multiplies the skin materials by a faint
    // color and leaves the eyes, teeth, and tongue as they are. Both come from shared materials,
    // so bodies that look the same draw with the same materials; the default look without a
    // tint draws with the body's own materials.
    [DisallowMultipleComponent]
    public sealed class PlayerBodyAppearance : MonoBehaviour
    {
        // How far the skin moves toward the tint color.
        public const float TintStrength = 0.35f;

        public const string CombinedRendererName = "CombinedBody";

        [SerializeField] private Material[] skinMaterials = new Material[0];
        [SerializeField] private BodyAppearanceCatalog catalog;

        private readonly Dictionary<Renderer, Material[]> ownMaterials = new Dictionary<Renderer, Material[]>();
        private SkinnedMeshRenderer combinedRenderer;
        private PlayerBodyMaterials materials;
        private BodyLook look = BodyLook.Default;
        private bool tinted;
        private Color tintColor = Color.white;

        public IReadOnlyList<Material> SkinMaterials
        {
            get { return skinMaterials; }
        }

        // The looks the body's model offers, or null.
        public BodyAppearanceCatalog Catalog
        {
            get { return catalog; }
        }

        public BodyLook Look
        {
            get { return look; }
        }

        // The renderer that draws the combined parts, or null before CombineParts.
        public SkinnedMeshRenderer CombinedRenderer
        {
            get { return combinedRenderer; }
        }

        // Draws the visible parts as one skinned renderer with the shared mesh of these parts,
        // and hides the parts. Equipment that hides a zone combines the body again. The look
        // and the tint stay.
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
            Refresh();

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

        // Shows the look, drawn with materials from the shared set.
        public void ApplyLook(BodyLook bodyLook, PlayerBodyMaterials bodyMaterials)
        {
            look = bodyLook ?? BodyLook.Default;
            materials = bodyMaterials;
            Refresh();
        }

        // Tints the skin toward the color, or takes the tint off when tinted is false.
        public void ApplyTint(bool isTinted, Color color, PlayerBodyMaterials bodyMaterials)
        {
            tinted = isTinted;
            tintColor = color;
            materials = bodyMaterials;
            Refresh();
        }

        // Every renderer draws with the materials of the look and the tint, from the body's
        // own materials.
        private void Refresh()
        {
            CaptureOwnMaterials();
            var tint = tinted ? TintFor(tintColor) : Color.white;
            foreach (var entry in ownMaterials)
            {
                if (entry.Key == null)
                {
                    continue;
                }

                if (materials == null)
                {
                    entry.Key.sharedMaterials = entry.Value;
                    continue;
                }

                var applied = (Material[])entry.Value.Clone();
                for (var index = 0; index < applied.Length; index++)
                {
                    var own = applied[index];
                    if (own == null)
                    {
                        continue;
                    }

                    var baseMap = catalog != null ? catalog.TextureFor(own, look) : null;
                    var skinTint = System.Array.IndexOf(skinMaterials, own) >= 0 ? tint : Color.white;
                    applied[index] = materials.For(own, baseMap, skinTint);
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
