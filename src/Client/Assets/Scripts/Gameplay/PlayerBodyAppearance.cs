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
    // its socket, and the fingers of a hand that holds one close around its grip; the body holds the
    // bones of a skinned one, such as a bow, below its socket. A tool the look holds, such as a
    // pickaxe, makes the body play the tool's clip, and a tool held with both hands closes the other
    // hand too and pins it on the tool. A weapon with a hold clip, such as a staff, keeps its arm in
    // that pose, plays its attack clip when its attack starts, timed to strike when the attack
    // releases, and names where its projectile leaves it. The other hand takes a weapon whose attack
    // takes both hands, such as a greataxe, wherever the attack clip brings it to the haft, and the
    // drawing hand of a body that holds a bow draws its string.
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
        private ToolBones handBones;
        private CharacterLocomotionAnimator locomotion;
        // The bones of the skinned attachments the body holds, by attachment, below their sockets.
        private readonly Dictionary<string, Transform> heldBones = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private BowRig bow;
        private BowRig.DrawingArm drawingArm;
        private bool stringHeld;
        private Animator bodyAnimator;

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

        // The weapon the body attacks with, which is no tool: the main hand's, such as a staff, or
        // else one in the off hand with an attack clip, such as a bow; or null.
        public BodyAppearanceCatalog.Attachment Weapon
        {
            get { return weapon; }
        }

        // The rig of the bow the body holds, or null.
        public BowRig Bow
        {
            get { return bow; }
        }

        // The body plays its weapon's attack clip once, such as Cast, timed so that the clip strikes
        // or looses when the attack releases, in secondsUntilRelease, and faces the target while it
        // stands; a melee weapon in one main hand without a clip of its own plays OneHandStrike.
        // False when the body holds no such weapon or cannot play the clip.
        public bool PlayAttack(bool melee, float secondsUntilRelease, Transform target = null)
        {
            var body = Locomotion;
            if (weapon == null || body == null)
            {
                return false;
            }

            string clip;
            float release;
            if (weapon.AttackClip.Length > 0)
            {
                clip = weapon.AttackClip;
                release = weapon.AttackRelease;
            }
            else if (melee && !weapon.HoldsWithBothHands
                && string.Equals(weapon.Bone, HumanLocomotion.MainHandSocketBone, StringComparison.Ordinal))
            {
                clip = HumanLocomotion.OneHandStrikeClip;
                release = HumanLocomotion.OneHandStrikeReleaseSeconds;
            }
            else
            {
                return false;
            }

            if (!body.PlayAttack(clip, HumanLocomotion.AttackClipTime(release, secondsUntilRelease)))
            {
                return false;
            }

            if (target != null)
            {
                body.Face(target);
            }

            if (bow != null)
            {
                bow.StartShot();
            }

            return true;
        }

        // The body ends the attack clip it plays, or waits to play, as when the attack was cancelled
        // before it released.
        public void StopAttack()
        {
            var body = Locomotion;
            if (body != null)
            {
                body.StopAttack();
            }
        }

        // The drawing hand lets go of the string of the bow the body holds, as its arrow leaves.
        public void Loose()
        {
            if (bow != null)
            {
                bow.Loose();
            }
        }

        // Where an arrow lies on the string of the bow the body draws: the nock, the way the arrow
        // points, and the way its cock vane points, away from the bow toward the body's left; false
        // while no hand holds the string.
        public bool TryGetNockedArrow(out Vector3 nock, out Vector3 forward, out Vector3 up)
        {
            up = Vector3.up;
            if (bow == null || !bow.TryGetNock(out nock, out forward))
            {
                nock = default(Vector3);
                forward = Vector3.forward;
                return false;
            }

            var left = Vector3.ProjectOnPlane(-transform.right, forward);
            up = left.sqrMagnitude > 0.0001f ? left.normalized : Vector3.up;
            return true;
        }

        // Where the projectile of the weapon leaves it right now, such as the crystal of a staff;
        // false when the body holds no weapon that names a launch point.
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

        // The body plays the clip of the tool it holds, or stops playing one, and each hand's arm
        // holds the attachment in it in its pose, such as a bow in the off hand's.
        private void UseTool()
        {
            var body = Locomotion;
            if (body != null)
            {
                body.Use(tool != null ? tool.UseClip : null);
                body.Hold(HoldClipOn(HumanLocomotion.MainHandSocketBone), HumanLocomotion.MainHandSocketBone);
                body.Hold(HoldClipOn(HumanLocomotion.OffHandSocketBone), HumanLocomotion.OffHandSocketBone);
            }
        }

        private string HoldClipOn(string socketBone)
        {
            var holding = held.FirstOrDefault(attachment => !attachment.IsTool
                && string.Equals(attachment.Bone, socketBone, StringComparison.Ordinal));
            return holding != null ? holding.HoldClip : null;
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
            var heldWeapon = held.FirstOrDefault(attachment => !attachment.IsTool
                    && string.Equals(attachment.Bone, HumanLocomotion.MainHandSocketBone, StringComparison.Ordinal))
                ?? held.FirstOrDefault(attachment => !attachment.IsTool && attachment.AttackClip.Length > 0
                    && string.Equals(attachment.Bone, HumanLocomotion.OffHandSocketBone, StringComparison.Ordinal));
            if (heldWeapon != weapon)
            {
                weapon = heldWeapon;
                weaponSocket = null;
                bow = null;
                stringHeld = false;
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
            HoldSkinnedBones();
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
            if (bow == null && weapon != null && weapon.IsSkinned && heldBones.TryGetValue(weapon.Name, out var bones))
            {
                bow = BowRig.TryCreate(bones, bones.parent, weapon);
            }

            MoveCloth();
            Grip();
            Refresh();
        }

        // The body holds the bones of every skinned attachment it holds, such as a bow's, below the
        // attachment's socket, and lets go of the bones of those it holds no more.
        private void HoldSkinnedBones()
        {
            foreach (var name in heldBones.Keys.ToArray())
            {
                if (held.Any(attachment => attachment.IsSkinned && attachment.Name == name))
                {
                    continue;
                }

                var bones = heldBones[name];
                heldBones.Remove(name);
                if (bones != null)
                {
                    // Out of the body at once, so no other bones bind to them before they are gone.
                    bones.SetParent(null, false);
                    if (Application.isPlaying)
                    {
                        Destroy(bones.gameObject);
                    }
                    else
                    {
                        DestroyImmediate(bones.gameObject);
                    }
                }
            }

            foreach (var attachment in held)
            {
                if (!attachment.IsSkinned || heldBones.ContainsKey(attachment.Name))
                {
                    continue;
                }

                var socket = PlayerBodyMeshes.FindDescendant(transform, attachment.Bone);
                if (socket != null)
                {
                    heldBones[attachment.Name] = PlayerBodyMeshes.HoldBones(attachment, socket);
                }
            }
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

            // Most bodies hold no tool or weapon in both hands and no bow, so they pay nothing more
            // per frame.
            var body = Locomotion;
            if (tool != null && tool.HoldsWithBothHands)
            {
                PinOffHand(body != null ? body.UseWeight : 0f);
            }
            else if (weapon != null && weapon.HoldsWithBothHands)
            {
                var attacking = body != null && string.Equals(body.ActionClip, weapon.AttackClip, StringComparison.Ordinal);
                SetGripWeight(HumanLocomotion.OffHandGripLayer, PinOffHand(attacking ? body.ActionWeight : 0f));
            }

            if (bow != null)
            {
                DrawBow(body);
            }
        }

        // After the Animator posed the body, the other hand holds what both hands hold: a tool where
        // its clip puts that hand, by the share of the pose that comes from the tool's clip, or a
        // weapon whose attack takes both hands, such as a greataxe, wherever its attack clip brings
        // that hand near the haft, by the share of the pose that comes from the attack clip. Returns
        // how firmly the hand holds it, from 0 to 1.
        public float PinOffHand(float weight)
        {
            var both = tool != null && tool.HoldsWithBothHands ? tool
                : weapon != null && weapon.HoldsWithBothHands ? weapon
                : null;
            if (both == null || combinedRenderer == null || weight <= 0f)
            {
                return 0f;
            }

            if (handBones == null && !ToolBones.TryFind(GetComponent<Animator>(), transform, both.Bone, out handBones))
            {
                return 0f;
            }

            if (both == weapon)
            {
                weight *= OffHandToolGrip.NearWeight(handBones.Socket, both.OffHandGrip, handBones.OffSocket);
            }

            OffHandToolGrip.Pin(handBones.Socket, both.OffHandGrip, handBones.Shoulder, handBones.UpperArm,
                handBones.LowerArm, handBones.Hand, handBones.OffSocket, weight);
            return weight;
        }

        // The string of the bow follows the drawing hand while the body draws, until the bow looses
        // as its release arrives or its attack clip comes to the release, and the fingers of that
        // hand close on the string while they hold it.
        private void DrawBow(CharacterLocomotionAnimator body)
        {
            if (drawingArm == null)
            {
                drawingArm = BowRig.DrawingArm.Find(GetComponent<Animator>(), transform);
            }

            var time = 0f;
            var drawing = body != null && body.IsPlayingAttack(weapon.AttackClip, out time);
            if (drawing && time >= weapon.AttackRelease)
            {
                bow.Loose();
            }

            bow.Update(drawing, drawingArm, Time.deltaTime);

            if (bow.IsHeld != stringHeld)
            {
                stringHeld = bow.IsHeld;
                Grip();
            }
        }

        private void SetGripWeight(string layer, float weight)
        {
            if (bodyAnimator == null)
            {
                bodyAnimator = GetComponent<Animator>();
            }

            var index = bodyAnimator != null && bodyAnimator.isInitialized ? bodyAnimator.GetLayerIndex(layer) : -1;
            if (index >= 0)
            {
                bodyAnimator.SetLayerWeight(index, weight);
            }
        }

        // The bones that hold a tool or weapon with both hands: its socket and the other arm with its
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

        // The fingers of each hand whose socket holds an attachment close around its grip, both
        // hands around a tool held with both hands, and the drawing hand around a bow's string while
        // it holds it; the others play the clips as they are, and the other hand of a weapon whose
        // attack takes both hands closes as it takes the haft. An Animator that does not play yet
        // takes the weights in the next LateUpdate.
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
                        || (tool != null && tool.HoldsWithBothHands && layer == HumanLocomotion.OffHandGripLayer)
                        || (stringHeld && layer == HumanLocomotion.MainHandGripLayer));
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
