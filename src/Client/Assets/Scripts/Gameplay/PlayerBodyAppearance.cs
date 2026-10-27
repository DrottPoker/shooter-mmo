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
    // its chains. An attachment the look holds, such as a sword, is part of the combined renderer on
    // its socket, and the fingers of a hand that holds one close around its grip. A tool the look
    // holds, such as a pickaxe, makes the body play the tool's clip, and a tool held with both hands
    // closes the other hand too and pins it on the tool. A weapon with a hold clip, such as a staff,
    // keeps its arm in that pose, plays its attack clip when its attack releases, and names where
    // its projectile leaves it.
    [DisallowMultipleComponent]
    public sealed class PlayerBodyAppearance : MonoBehaviour
    {
        // How far the skin moves toward the tint color.
        public const float TintStrength = 0.35f;

        public const string CombinedRendererName = "CombinedBody";

        private static readonly BodyAppearanceCatalog.Garment[] NoGarments = new BodyAppearanceCatalog.Garment[0];

        private static readonly string[] GripLayers = { HumanLocomotion.MainHandGripLayer, HumanLocomotion.OffHandGripLayer };

        [SerializeField] private Material[] skinMaterials = new Material[0];
        [SerializeField] private BodyAppearanceCatalog catalog;

        private readonly Dictionary<Renderer, Material[]> ownMaterials = new Dictionary<Renderer, Material[]>();
        private readonly List<BodyAppearanceCatalog.Paint> paints = new List<BodyAppearanceCatalog.Paint>();
        private readonly List<BodyAppearanceCatalog.Garment> worn = new List<BodyAppearanceCatalog.Garment>();
        private List<BodyAppearanceCatalog.Attachment> held = new List<BodyAppearanceCatalog.Attachment>();
        private SkinnedMeshRenderer[] parts = new SkinnedMeshRenderer[0];
        private PlayerBodyMeshes meshes;
        private string wornKey = string.Empty;
        private SkinnedMeshRenderer combinedRenderer;
        private PlayerBodyMaterials materials;
        private BodyLook look = BodyLook.Default;
        private bool gripPending;
        private bool tinted;
        private Color tintColor = Color.white;
        private BodyAppearanceCatalog.Attachment tool;
        private BodyAppearanceCatalog.Attachment weapon;
        private Transform weaponSocket;
        private ToolBones toolBones;
        private CharacterLocomotionAnimator locomotion;

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

        // The attachments the body holds, one per socket, as the look names them.
        public IReadOnlyList<BodyAppearanceCatalog.Attachment> Held
        {
            get { return held; }
        }

        // The tool among them, such as a pickaxe while the character gathers, or null.
        public BodyAppearanceCatalog.Attachment Tool
        {
            get { return tool; }
        }

        // The weapon in the main hand that is no tool, such as a staff, or null.
        public BodyAppearanceCatalog.Attachment Weapon
        {
            get { return weapon; }
        }

        // The body plays the attack clip of the weapon in its main hand once, such as Cast; false
        // when it holds no weapon with one.
        public bool PlayAttack()
        {
            var body = Locomotion;
            return weapon != null && weapon.AttackClip.Length > 0 && body != null && body.PlayAttack(weapon.AttackClip);
        }

        // Where the projectile of the weapon in the main hand leaves it right now, such as the
        // crystal of a staff; false when the body holds no weapon that names a launch point.
        public bool TryGetLaunchPoint(out Vector3 position)
        {
            position = default(Vector3);
            if (weapon == null || !weapon.HasLaunchPoint || combinedRenderer == null)
            {
                return false;
            }

            if (weaponSocket == null)
            {
                weaponSocket = PlayerBodyMeshes.FindDescendant(transform, weapon.Bone);
            }

            if (weaponSocket == null)
            {
                return false;
            }

            position = weaponSocket.TransformPoint(weapon.LaunchPoint);
            return true;
        }

        private CharacterLocomotionAnimator Locomotion
        {
            get
            {
                if (locomotion == null)
                {
                    locomotion = GetComponent<CharacterLocomotionAnimator>();
                }

                return locomotion;
            }
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

            Grip();
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
            var changed = WearLook();
            UseTool();
            if (changed && combinedRenderer != null)
            {
                Assemble();
                return;
            }

            Refresh();
        }

        // The body plays the clip of the tool it holds, or stops playing one, and holds its weapon
        // in the weapon's pose.
        private void UseTool()
        {
            var body = Locomotion;
            if (body != null)
            {
                body.Use(tool != null ? tool.UseClip : null);
                body.Hold(weapon != null ? weapon.HoldClip : null);
            }
        }

        // The garments of the catalog that the look names, from the lowest layer up, and its
        // attachments; true when they changed. A body wears one garment per visual slot, such as one
        // chest piece: of two of a slot that the look names, the first in the catalog is worn. It
        // holds one attachment per socket the same way.
        private bool WearLook()
        {
            worn.Clear();
            held = new List<BodyAppearanceCatalog.Attachment>();
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

                held = PlayerBodyMeshes.Held(catalog.Attachments.Where(attachment =>
                    attachment != null && look.HasAttachment(attachment.Name)).ToList());
            }

            tool = held.FirstOrDefault(attachment => attachment.IsTool);
            var mainHandWeapon = held.FirstOrDefault(attachment => !attachment.IsTool
                && string.Equals(attachment.Bone, HumanLocomotion.MainHandSocketBone, StringComparison.Ordinal));
            if (mainHandWeapon != weapon)
            {
                weapon = mainHandWeapon;
                weaponSocket = null;
            }

            var key = string.Join("+", worn.Select(garment => garment.Name))
                + "#" + string.Join("+", held.Select(attachment => attachment.Name));
            var changed = key != wornKey;
            wornKey = key;
            return changed;
        }

        // The combined renderer draws the shared mesh of the parts, the worn garments, and the held
        // attachments.
        private void Assemble()
        {
            var combined = meshes.For(parts, transform, catalog, worn, held);
            var transforms = new Dictionary<string, Transform>();
            foreach (var child in GetComponentsInChildren<Transform>(true))
            {
                transforms[child.name] = child;
            }

            // Unity 6000.6 stops drawing an enabled skinned renderer whose mesh changes to one of
            // another size; a renderer that is off while it changes draws the new mesh.
            var enabled = combinedRenderer.enabled;
            combinedRenderer.enabled = false;
            combinedRenderer.sharedMesh = combined.Mesh;
            combinedRenderer.bones = combined.BoneNames.Select(boneName => transforms[boneName]).ToArray();
            combinedRenderer.rootBone = transforms.TryGetValue(combined.RootBoneName, out var rootBone)
                ? rootBone
                : transform;
            combinedRenderer.localBounds = combined.LocalBounds;
            combinedRenderer.enabled = enabled;
            combinedRenderer.sharedMaterials = combined.Materials.ToArray();
            ownMaterials[combinedRenderer] = combined.Materials.ToArray();
            MoveCloth();
            Grip();
            Refresh();
        }

        // An Animator that is enabled again starts from its controller's layer weights.
        private void OnEnable()
        {
            gripPending = true;
        }

        private void LateUpdate()
        {
            if (gripPending)
            {
                Grip();
            }

            // Most bodies hold no tool, so they pay nothing more per frame.
            if (tool != null && tool.HoldsWithBothHands)
            {
                var body = Locomotion;
                PinOffHand(body != null ? body.UseWeight : 0f);
            }
        }

        // After the Animator posed the body, the other hand of a tool held with both hands holds
        // the tool where its clip puts that hand, by the share of the pose that comes from the
        // tool's clip.
        public void PinOffHand(float weight)
        {
            if (tool == null || !tool.HoldsWithBothHands || combinedRenderer == null || weight <= 0f)
            {
                return;
            }

            if (toolBones == null && !ToolBones.TryFind(GetComponent<Animator>(), transform, tool.Bone, out toolBones))
            {
                return;
            }

            OffHandToolGrip.Pin(toolBones.Socket, tool.OffHandGrip, toolBones.Shoulder, toolBones.UpperArm,
                toolBones.LowerArm, toolBones.Hand, toolBones.OffSocket, weight);
        }

        // The bones that hold a tool with both hands: the tool's socket and the other arm with its
        // collar bone, if the body has one, and its socket.
        private sealed class ToolBones
        {
            public Transform Socket;
            public Transform Shoulder;
            public Transform UpperArm;
            public Transform LowerArm;
            public Transform Hand;
            public Transform OffSocket;

            public static bool TryFind(Animator animator, Transform root, string socketBone, out ToolBones bones)
            {
                bones = null;
                if (animator == null || !animator.isHuman
                    || !string.Equals(socketBone, HumanLocomotion.MainHandSocketBone, StringComparison.Ordinal))
                {
                    return false;
                }

                var found = new ToolBones
                {
                    Socket = PlayerBodyMeshes.FindDescendant(root, HumanLocomotion.MainHandSocketBone),
                    Shoulder = animator.GetBoneTransform(HumanBodyBones.LeftShoulder),
                    UpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm),
                    LowerArm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm),
                    Hand = animator.GetBoneTransform(HumanBodyBones.LeftHand),
                    OffSocket = PlayerBodyMeshes.FindDescendant(root, HumanLocomotion.OffHandSocketBone),
                };
                if (found.Socket == null || found.UpperArm == null || found.LowerArm == null
                    || found.Hand == null || found.OffSocket == null)
                {
                    return false;
                }

                bones = found;
                return true;
            }
        }

        // The fingers of each hand whose socket holds an attachment close around its grip, and
        // both hands around a tool held with both hands; the others play the clips as they are.
        // An Animator that does not play yet takes the weights in the next LateUpdate.
        private void Grip()
        {
            var animator = GetComponent<Animator>();
            gripPending = false;
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                return;
            }

            if (!animator.isActiveAndEnabled || !animator.isInitialized)
            {
                gripPending = true;
                return;
            }

            foreach (var layer in GripLayers)
            {
                var index = animator.GetLayerIndex(layer);
                if (index >= 0)
                {
                    var holds = combinedRenderer != null && (held.Any(attachment =>
                        string.Equals(HumanLocomotion.GripLayerFor(attachment.Bone), layer, StringComparison.Ordinal))
                        || (tool != null && tool.HoldsWithBothHands && layer == HumanLocomotion.OffHandGripLayer));
                    animator.SetLayerWeight(index, holds ? 1f : 0f);
                }
            }
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
