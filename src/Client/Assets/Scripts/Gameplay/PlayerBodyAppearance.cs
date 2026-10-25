using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The look of a player body. Its visible skinned parts and the garments it wears are drawn as
    // one combined renderer, put together again when the garments change. A look from the body's
    // appearance catalog shows other images on its painted materials, such as another face or eye
    // color, paint layers over its skin, and garments, and a tint multiplies the skin materials by
    // a faint color and leaves the eyes, teeth, tongue, and garments as they are. Both come from
    // shared materials, so bodies that look the same draw with the same materials; the default
    // look without a tint draws with the body's own materials. A garment draws in the look's dye
    // colors or its natural ones. A garment with loose cloth lets the body's ClothChainSolver move
    // its chains.
    [DisallowMultipleComponent]
    public sealed class PlayerBodyAppearance : MonoBehaviour
    {
        // How far the skin moves toward the tint color.
        public const float TintStrength = 0.35f;

        public const string CombinedRendererName = "CombinedBody";

        private static readonly BodyAppearanceCatalog.Garment[] NoGarments = new BodyAppearanceCatalog.Garment[0];

        [SerializeField] private Material[] skinMaterials = new Material[0];
        [SerializeField] private BodyAppearanceCatalog catalog;

        private readonly Dictionary<Renderer, Material[]> ownMaterials = new Dictionary<Renderer, Material[]>();
        private readonly List<BodyAppearanceCatalog.Paint> paints = new List<BodyAppearanceCatalog.Paint>();
        private readonly List<BodyAppearanceCatalog.Garment> worn = new List<BodyAppearanceCatalog.Garment>();
        private SkinnedMeshRenderer[] parts = new SkinnedMeshRenderer[0];
        private PlayerBodyMeshes meshes;
        private string wornKey = string.Empty;
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

        // The garments the body wears, from the lowest layer up, as the look names them.
        public IReadOnlyList<BodyAppearanceCatalog.Garment> Worn
        {
            get { return worn; }
        }

        // Draws the visible parts and the worn garments as one skinned renderer with the shared
        // mesh of these parts and garments, and hides the parts. The look and the tint stay.
        public void CombineParts(PlayerBodyMeshes bodyMeshes)
        {
            if (bodyMeshes == null || combinedRenderer != null)
            {
                return;
            }

            RestoreOwnMaterials();
            parts = GetComponentsInChildren<SkinnedMeshRenderer>(false)
                .Where(part => part.enabled && part.sharedMesh != null)
                .ToArray();
            if (parts.Length == 0)
            {
                return;
            }

            meshes = bodyMeshes;
            var rendererObject = new GameObject(CombinedRendererName);
            rendererObject.transform.SetParent(transform, false);
            combinedRenderer = rendererObject.AddComponent<SkinnedMeshRenderer>();
            combinedRenderer.shadowCastingMode = parts[0].shadowCastingMode;
            combinedRenderer.receiveShadows = parts[0].receiveShadows;
            combinedRenderer.quality = parts[0].quality;
            combinedRenderer.updateWhenOffscreen = false;
            foreach (var part in parts)
            {
                part.enabled = false;
            }

            ownMaterials.Clear();
            WearLook();
            Assemble();

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

        // Shows the look, drawn with materials from the shared set, and puts the body together
        // again when the look wears other garments.
        public void ApplyLook(BodyLook bodyLook, PlayerBodyMaterials bodyMaterials)
        {
            look = bodyLook ?? BodyLook.Default;
            materials = bodyMaterials;
            if (WearLook() && combinedRenderer != null)
            {
                Assemble();
                return;
            }

            Refresh();
        }

        // The garments of the catalog that the look names, from the lowest layer up; true when
        // they changed. A body wears one garment per visual slot, such as one chest piece: of two
        // of a slot that the look names, the first in the catalog is worn.
        private bool WearLook()
        {
            worn.Clear();
            if (catalog != null)
            {
                var slots = new HashSet<string>(StringComparer.Ordinal);
                foreach (var garment in catalog.Garments)
                {
                    if (garment != null && look.HasGarment(garment.Name) && slots.Add(garment.Slot))
                    {
                        worn.Add(garment);
                    }
                }
            }

            var key = string.Join("+", worn.Select(garment => garment.Name));
            var changed = key != wornKey;
            wornKey = key;
            return changed;
        }

        // The combined renderer draws the shared mesh of the parts and the worn garments.
        private void Assemble()
        {
            var combined = meshes.For(parts, transform, catalog, worn);
            var transforms = new Dictionary<string, Transform>();
            foreach (var child in GetComponentsInChildren<Transform>(true))
            {
                transforms[child.name] = child;
            }

            combinedRenderer.sharedMesh = combined.Mesh;
            combinedRenderer.bones = combined.BoneNames.Select(boneName => transforms[boneName]).ToArray();
            combinedRenderer.rootBone = transforms.TryGetValue(combined.RootBoneName, out var rootBone)
                ? rootBone
                : transform;
            combinedRenderer.localBounds = combined.LocalBounds;
            combinedRenderer.sharedMaterials = combined.Materials.ToArray();
            ownMaterials[combinedRenderer] = combined.Materials.ToArray();
            MoveCloth();
            Refresh();
        }

        // The chains of the worn garments' cloth move, each with its garment's model, so a cloak lies
        // over a robe's skirt; the others rest.
        private void MoveCloth()
        {
            var solver = GetComponent<ClothChainSolver>();
            if (solver == null)
            {
                return;
            }

            foreach (ClothChainKind kind in Enum.GetValues(typeof(ClothChainKind)))
            {
                var garment = PlayerBodyMeshes.Drawn(worn).LastOrDefault(candidate => ChainsOf(candidate.Cloth) == kind);
                if (garment != null)
                {
                    solver.Activate(kind, ClothProfile.For(kind), garment.Model);
                }
                else
                {
                    solver.Deactivate(kind);
                }
            }
        }

        // A robe hangs on the skirt chains, a cloak on the cloak chains, and a hood on the hood chain.
        public static ClothChainKind? ChainsOf(string cloth)
        {
            switch (cloth)
            {
                case "robe":
                    return ClothChainKind.Skirt;
                case "cloak":
                    return ClothChainKind.Cloak;
                case "hood":
                    return ClothChainKind.Hood;
                default:
                    return null;
            }
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
            // Garments show only on the combined renderer, and only those drawn hide underwear.
            var drawn = combinedRenderer != null ? PlayerBodyMeshes.Drawn(worn) : (IReadOnlyList<BodyAppearanceCatalog.Garment>)NoGarments;
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
                    paints.Clear();
                    if (catalog != null)
                    {
                        catalog.PaintsFor(own, look, drawn, paints);
                    }

                    var skinTint = System.Array.IndexOf(skinMaterials, own) >= 0 ? tint : Color.white;
                    // A worn garment draws with its texture's material, in the look's dye colors.
                    var garment = worn.FirstOrDefault(candidate => candidate.Material == own);
                    var dyes = garment != null ? look.DyesFor(garment.Name) : null;
                    applied[index] = materials.For(own, baseMap, skinTint, paints, dyes);
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
