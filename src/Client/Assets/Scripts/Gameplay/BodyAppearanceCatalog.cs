using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The looks a body model offers, from the approved exports of its art assets: one slot per
    // painted material that can show other images, such as the Face and the Eye, with its
    // options, the material's own image first, the paint layers that fit the body, such as its
    // underwear and a tattoo, in the order they are drawn, the garments that fit it, with the faces
    // of the body's editable mesh that each part's triangles come from, and the attachments its
    // sockets can hold, such as a sword. MMORPG > Tools > Art Pipeline > Update Appearance Catalogs
    // writes it from asset.json; see docs/ART_PIPELINE.md and docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class BodyAppearanceCatalog : ScriptableObject
    {
        [SerializeField] private Slot[] slots = Array.Empty<Slot>();
        [SerializeField] private Paint[] paints = Array.Empty<Paint>();
        [SerializeField] private Part[] parts = Array.Empty<Part>();
        [SerializeField] private Garment[] garments = Array.Empty<Garment>();
        [SerializeField] private Attachment[] attachments = Array.Empty<Attachment>();

        public IReadOnlyList<Slot> Slots
        {
            get { return slots; }
        }

        // In the order they are drawn, the first one lowest.
        public IReadOnlyList<Paint> Paints
        {
            get { return paints; }
        }

        // The body parts whose faces garments can cover, such as Male_Torso.
        public IReadOnlyList<Part> Parts
        {
            get { return parts; }
        }

        // From the lowest layer up.
        public IReadOnlyList<Garment> Garments
        {
            get { return garments; }
        }

        // By name.
        public IReadOnlyList<Attachment> Attachments
        {
            get { return attachments ?? Array.Empty<Attachment>(); }
        }

        public void Configure(Slot[] catalogSlots, Paint[] catalogPaints = null, Part[] catalogParts = null,
            Garment[] catalogGarments = null, Attachment[] catalogAttachments = null)
        {
            slots = catalogSlots ?? Array.Empty<Slot>();
            paints = catalogPaints ?? Array.Empty<Paint>();
            parts = catalogParts ?? Array.Empty<Part>();
            garments = catalogGarments ?? Array.Empty<Garment>();
            attachments = catalogAttachments ?? Array.Empty<Attachment>();
        }

        public Attachment AttachmentNamed(string name)
        {
            foreach (var attachment in Attachments)
            {
                if (attachment != null && string.Equals(attachment.Name, name, StringComparison.Ordinal))
                {
                    return attachment;
                }
            }

            return null;
        }

        public Part PartNamed(string name)
        {
            foreach (var part in parts)
            {
                if (part != null && string.Equals(part.Name, name, StringComparison.Ordinal))
                {
                    return part;
                }
            }

            return null;
        }

        // The other garments of the catalog in the same visual slot as this one, such as another
        // chest piece, which a body never wears together with it.
        public IEnumerable<Garment> OthersInSlot(Garment garment)
        {
            if (garment == null)
            {
                yield break;
            }

            foreach (var other in garments)
            {
                if (other != null && other != garment && other.Slot == garment.Slot)
                {
                    yield return other;
                }
            }
        }

        public Garment GarmentNamed(string name)
        {
            foreach (var garment in garments)
            {
                if (garment != null && string.Equals(garment.Name, name, StringComparison.Ordinal))
                {
                    return garment;
                }
            }

            return null;
        }

        // The paint layers a body shows on the material with the look and these drawn garments, in
        // the order they are drawn.
        public void PaintsFor(Material material, BodyLook look, IReadOnlyList<Garment> drawn, List<Paint> result)
        {
            result.Clear();
            if (material == null)
            {
                return;
            }

            ShownPaints(look, drawn, result);
            result.RemoveAll(paint => paint.Material != material);
        }

        // The paint layers a body shows with the look and these drawn garments, in the order they
        // are drawn, by AppearanceRules: of each category and slot the first the look names, the
        // body's first underwear of a slot when the look names none, since underwear is always
        // worn, and no underwear of a slot whose drawn garment hides it.
        public void ShownPaints(BodyLook look, IReadOnlyList<Garment> drawn, List<Paint> result)
        {
            result.Clear();
            var taken = new HashSet<(AppearanceCategory, string)>();
            var shown = new HashSet<Paint>();
            foreach (var named in new[] { true, false })
            {
                foreach (var paint in paints)
                {
                    if (paint == null || taken.Contains((paint.Category, paint.Slot)))
                    {
                        continue;
                    }

                    if (named ? look != null && look.HasPaint(paint.Name) : AppearanceRules.For(paint.Category).Required)
                    {
                        taken.Add((paint.Category, paint.Slot));
                        shown.Add(paint);
                    }
                }
            }

            foreach (var paint in paints)
            {
                if (shown.Contains(paint) && !(paint.Category == AppearanceCategory.Underwear && HidesUnderwear(drawn, paint.Slot)))
                {
                    result.Add(paint);
                }
            }
        }

        // The other paint layers of the catalog of the same category and slot as this one, such as
        // another tattoo, which a body never shows together with it.
        public IEnumerable<Paint> OthersLike(Paint paint)
        {
            if (paint == null)
            {
                yield break;
            }

            foreach (var other in paints)
            {
                if (other != null && other != paint && other.Category == paint.Category && other.Slot == paint.Slot)
                {
                    yield return other;
                }
            }
        }

        private static bool HidesUnderwear(IReadOnlyList<Garment> drawn, string slot)
        {
            if (drawn == null)
            {
                return false;
            }

            foreach (var garment in drawn)
            {
                if (garment != null && garment.HidesUnderwear && garment.Slot == slot)
                {
                    return true;
                }
            }

            return false;
        }

        public Slot SlotNamed(string name)
        {
            foreach (var slot in slots)
            {
                if (slot != null && string.Equals(slot.Name, name, StringComparison.Ordinal))
                {
                    return slot;
                }
            }

            return null;
        }

        // The image the material shows in the look: the chosen option's, or null for the
        // material's own image, when the material has no slot, or the look names no option of it.
        public Texture2D TextureFor(Material material, BodyLook look)
        {
            if (material == null || look == null)
            {
                return null;
            }

            foreach (var slot in slots)
            {
                if (slot == null || slot.Material != material)
                {
                    continue;
                }

                var options = slot.Options;
                var chosen = look.OptionFor(slot.Name);
                for (var index = 1; index < options.Count; index++)
                {
                    if (string.Equals(options[index].Name, chosen, StringComparison.Ordinal))
                    {
                        return options[index].Texture;
                    }
                }

                return null;
            }

            return null;
        }

        [Serializable]
        public sealed class Slot
        {
            [SerializeField] private string name;
            [SerializeField] private Material material;
            [SerializeField] private Option[] options;

            public Slot(string name, Material material, Option[] options)
            {
                this.name = name;
                this.material = material;
                this.options = options ?? Array.Empty<Option>();
            }

            // Such as Face, from the material M_Face_Male.
            public string Name
            {
                get { return name; }
            }

            public Material Material
            {
                get { return material; }
            }

            // The material's own image first.
            public IReadOnlyList<Option> Options
            {
                get { return options ?? Array.Empty<Option>(); }
            }
        }

        [Serializable]
        public sealed class Paint
        {
            [SerializeField] private string name;
            [SerializeField] private AppearanceCategory category;
            [SerializeField] private string slot;
            [SerializeField] private Material material;
            [SerializeField] private Texture2D texture;
            [SerializeField] private Vector4 rect = new Vector4(0f, 0f, 1f, 1f);
            [SerializeField] private int layer;
            [SerializeField] private float smoothness;
            [SerializeField] private float metallic;

            public Paint(string name, AppearanceCategory category, string slot, Material material, Texture2D texture, Vector4 rect,
                int layer, float smoothness, float metallic)
            {
                this.name = name;
                this.category = category;
                this.slot = slot ?? string.Empty;
                this.material = material;
                this.texture = texture;
                this.rect = rect;
                this.layer = layer;
                this.smoothness = smoothness;
                this.metallic = metallic;
            }

            // Such as Tattoo, from the paint layer art/Paint/Tattoo.
            public string Name
            {
                get { return name; }
            }

            // Underwear, Scar, or Tattoo.
            public AppearanceCategory Category
            {
                get { return category; }
            }

            // Underwear only: the visual slot it is worn in, body for a bra or legs for briefs;
            // empty otherwise.
            public string Slot
            {
                get { return slot ?? string.Empty; }
            }

            // The body material it paints over, such as M_Body_Male.
            public Material Material
            {
                get { return material; }
            }

            // The paint in the mesh's paint UVs, its second UV map, cropped to the paint and
            // transparent where the skin shows.
            public Texture2D Texture
            {
                get { return texture; }
            }

            // Where the texture lies in the paint UVs: xy its corner and zw its size.
            public Vector4 Rect
            {
                get { return rect; }
            }

            // Paint layers draw from the lowest layer up.
            public int Layer
            {
                get { return layer; }
            }

            public float Smoothness
            {
                get { return smoothness; }
            }

            public float Metallic
            {
                get { return metallic; }
            }
        }

        // A body part's mesh in the faces of the body's editable mesh: the face each of its triangles
        // comes from, over its submeshes in order, and the vertex each of its vertices is.
        [Serializable]
        public sealed class Part
        {
            [SerializeField] private string name;
            [SerializeField] private int[] triangleFaces;
            [SerializeField] private int[] vertexSources;

            public Part(string name, int[] triangleFaces, int[] vertexSources)
            {
                this.name = name;
                this.triangleFaces = triangleFaces ?? Array.Empty<int>();
                this.vertexSources = vertexSources ?? Array.Empty<int>();
            }

            // The name of the part's renderer, such as Male_Torso.
            public string Name
            {
                get { return name; }
            }

            public IReadOnlyList<int> TriangleFaces
            {
                get { return triangleFaces ?? Array.Empty<int>(); }
            }

            public IReadOnlyList<int> VertexSources
            {
                get { return vertexSources ?? Array.Empty<int>(); }
            }
        }

        // A garment the body can wear: a garment texture on its template's version for the body,
        // with the template's model, skinned to the body's armature; its visual slot and layer; the
        // slots it hides; the body faces it covers; the body face under each of its triangles; how
        // far it moves visible body vertices inward; and the texture's material it draws with.
        [Serializable]
        public sealed class Garment
        {
            // The visual slot of hair, worn as a garment under headwear.
            public const string HairSlot = "hair";

            // What headwear does to the hair under it: hides it, or presses it flat into its form
            // under headwear; any other value leaves it as it is.
            public const string HideHair = "hide";
            public const string CompressHair = "compress";

            // The blend shape of hair that is its form pressed flat under headwear.
            public const string UnderHeadwearShape = "UnderHeadwear";

            [SerializeField] private string name;
            [SerializeField] private string template;
            [SerializeField] private Material material;
            [SerializeField] private string slot;
            [SerializeField] private int layer;
            [SerializeField] private string[] hides;
            [SerializeField] private bool hidesUnderwear;
            [SerializeField] private string hair;
            [SerializeField] private string cloth;
            [SerializeField] private GameObject model;
            [SerializeField] private int[] covered;
            [SerializeField] private int[] underlay;
            [SerializeField] private int[] pullInVertices;
            [SerializeField] private float[] pullInDistances;

            public Garment(string name, string template, Material material, string slot, int layer, string[] hides,
                bool hidesUnderwear, string hair, string cloth, GameObject model, int[] covered, int[] underlay,
                int[] pullInVertices, float[] pullInDistances)
            {
                this.name = name;
                this.template = template ?? string.Empty;
                this.material = material;
                this.slot = slot;
                this.layer = layer;
                this.hides = hides ?? Array.Empty<string>();
                this.hidesUnderwear = hidesUnderwear;
                this.hair = hair ?? string.Empty;
                this.cloth = cloth ?? string.Empty;
                this.model = model;
                this.covered = covered ?? Array.Empty<int>();
                this.underlay = underlay ?? Array.Empty<int>();
                this.pullInVertices = pullInVertices ?? Array.Empty<int>();
                this.pullInDistances = pullInDistances ?? Array.Empty<float>();
            }

            // Such as LeatherJerkin, from the garment texture art/GarmentTextures/LeatherJerkin.
            public string Name
            {
                get { return name; }
            }

            // Such as Jerkin, from the garment template art/Garments/Jerkin.
            public string Template
            {
                get { return template ?? string.Empty; }
            }

            // The texture's material, which the template's model draws with instead of its own; null
            // draws with the model's.
            public Material Material
            {
                get { return material; }
            }

            // The visual slot, such as body.
            public string Slot
            {
                get { return slot; }
            }

            // Garments of a character are ordered by layer, from the inside out.
            public int Layer
            {
                get { return layer; }
            }

            public IReadOnlyList<string> Hides
            {
                get { return hides ?? Array.Empty<string>(); }
            }

            // Whether the underwear of its slot is left off while it is drawn, such as the bra under
            // a top that bares the shoulders.
            public bool HidesUnderwear
            {
                get { return hidesUnderwear; }
            }

            // Headwear only: hide, compress, or show the hair under it; empty otherwise.
            public string Hair
            {
                get { return hair ?? string.Empty; }
            }

            // The cloth profile of its loose parts, robe, cloak, or hood, or empty.
            public string Cloth
            {
                get { return cloth ?? string.Empty; }
            }

            // The garment's model for this body, with its renderer and the body's armature.
            public GameObject Model
            {
                get { return model; }
            }

            // The faces of the body's editable mesh it hides, sorted.
            public IReadOnlyList<int> Covered
            {
                get { return covered ?? Array.Empty<int>(); }
            }

            // For each triangle of its renderer, over the submeshes in order, the body face under it.
            public IReadOnlyList<int> Underlay
            {
                get { return underlay ?? Array.Empty<int>(); }
            }

            public IReadOnlyList<int> PullInVertices
            {
                get { return pullInVertices ?? Array.Empty<int>(); }
            }

            // In meters, per pulled-in vertex.
            public IReadOnlyList<float> PullInDistances
            {
                get { return pullInDistances ?? Array.Empty<float>(); }
            }
        }

        // A static model a socket of the body holds, such as a sword in the right hand: its model,
        // whose root object Grip lies on the socket bone, the socket, the equipment slot whose item
        // it shows, and the bone. A tool, such as a pickaxe, is held only while the body uses it:
        // it names the clip the body plays meanwhile, and a tool held in both hands where on it the
        // other hand holds it. A weapon, such as a staff, may name the pose its arm holds it in,
        // the clip the body plays once when its attack releases, and where its projectile leaves it.
        [Serializable]
        public sealed class Attachment
        {
            [SerializeField] private string name;
            [SerializeField] private string socket;
            [SerializeField] private string bone;
            [SerializeField] private GameObject model;
            [SerializeField] private string useClip;
            [SerializeField] private bool holdsWithBothHands;
            [SerializeField] private Vector3 offHandGrip;
            [SerializeField] private string holdClip;
            [SerializeField] private string attackClip;
            [SerializeField] private bool hasLaunchPoint;
            [SerializeField] private Vector3 launchPoint;

            public Attachment(string name, string socket, string bone, GameObject model,
                string useClip = null, Vector3? offHandGrip = null,
                string holdClip = null, string attackClip = null, Vector3? launchPoint = null)
            {
                this.name = name;
                this.socket = socket ?? string.Empty;
                this.bone = bone ?? string.Empty;
                this.model = model;
                this.useClip = useClip ?? string.Empty;
                holdsWithBothHands = offHandGrip.HasValue;
                this.offHandGrip = offHandGrip ?? Vector3.zero;
                this.holdClip = holdClip ?? string.Empty;
                this.attackClip = attackClip ?? string.Empty;
                hasLaunchPoint = launchPoint.HasValue;
                this.launchPoint = launchPoint ?? Vector3.zero;
            }

            // Such as IronSword, from the attachment art/Attachments/IronSword.
            public string Name
            {
                get { return name; }
            }

            // Such as main_hand.
            public string Socket
            {
                get { return socket ?? string.Empty; }
            }

            // Such as Socket_MainHand.
            public string Bone
            {
                get { return bone ?? string.Empty; }
            }

            public GameObject Model
            {
                get { return model; }
            }

            // The clip the body plays while it uses the tool, such as Mine, or empty for an
            // attachment that is no tool.
            public string UseClip
            {
                get { return useClip ?? string.Empty; }
            }

            public bool IsTool
            {
                get { return UseClip.Length > 0; }
            }

            // Whether the other hand holds the tool too, at OffHandGrip.
            public bool HoldsWithBothHands
            {
                get { return holdsWithBothHands; }
            }

            // Where on the model the other hand's socket holds it, in the model's and so the
            // socket's space; that socket is turned half around the forward axis from this one, so
            // both thumbs point along the haft.
            public Vector3 OffHandGrip
            {
                get { return offHandGrip; }
            }

            // The pose the arm holds the weapon in, such as HoldStaff, or empty for the hand pose
            // alone.
            public string HoldClip
            {
                get { return holdClip ?? string.Empty; }
            }

            // The clip the body plays once whenever its attack releases, such as Cast, or empty.
            public string AttackClip
            {
                get { return attackClip ?? string.Empty; }
            }

            // Whether the weapon names where the projectile of its attack leaves it, at LaunchPoint.
            public bool HasLaunchPoint
            {
                get { return hasLaunchPoint; }
            }

            // Where on the model the projectile leaves, in the model's and so the socket's space,
            // such as the crystal of a staff.
            public Vector3 LaunchPoint
            {
                get { return launchPoint; }
            }
        }

        [Serializable]
        public sealed class Option
        {
            [SerializeField] private string name;
            [SerializeField] private Texture2D texture;

            public Option(string name, Texture2D texture)
            {
                this.name = name;
                this.texture = texture;
            }

            // Such as Clean, from the image T_Face_Male_Clean.
            public string Name
            {
                get { return name; }
            }

            public Texture2D Texture
            {
                get { return texture; }
            }
        }
    }
}
