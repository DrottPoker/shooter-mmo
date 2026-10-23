using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The looks a body model offers, from the approved exports of its art assets: one slot per
    // painted material that can show other images, such as the Face and the Eye, with its
    // options, the material's own image first, the paint layers that fit the body, such as the
    // paint of a leather jerkin, in the order they are drawn, and the garments that fit it, with
    // the faces of the body's editable mesh that each part's triangles come from. MMORPG > Tools >
    // Art Pipeline > Update Appearance Catalogs writes it from asset.json; see
    // docs/ART_PIPELINE.md and docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class BodyAppearanceCatalog : ScriptableObject
    {
        [SerializeField] private Slot[] slots = Array.Empty<Slot>();
        [SerializeField] private Paint[] paints = Array.Empty<Paint>();
        [SerializeField] private Part[] parts = Array.Empty<Part>();
        [SerializeField] private Garment[] garments = Array.Empty<Garment>();

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

        public void Configure(Slot[] catalogSlots, Paint[] catalogPaints = null, Part[] catalogParts = null,
            Garment[] catalogGarments = null)
        {
            slots = catalogSlots ?? Array.Empty<Slot>();
            paints = catalogPaints ?? Array.Empty<Paint>();
            parts = catalogParts ?? Array.Empty<Part>();
            garments = catalogGarments ?? Array.Empty<Garment>();
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

        // The paint layers of the look on the material, in the order they are drawn.
        public void PaintsFor(Material material, BodyLook look, List<Paint> result)
        {
            result.Clear();
            if (material == null || look == null)
            {
                return;
            }

            foreach (var paint in paints)
            {
                if (paint != null && paint.Material == material && look.HasPaint(paint.Name))
                {
                    result.Add(paint);
                }
            }
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
            [SerializeField] private Material material;
            [SerializeField] private Texture2D texture;
            [SerializeField] private int layer;
            [SerializeField] private float smoothness;
            [SerializeField] private float metallic;

            public Paint(string name, Material material, Texture2D texture, int layer, float smoothness, float metallic)
            {
                this.name = name;
                this.material = material;
                this.texture = texture;
                this.layer = layer;
                this.smoothness = smoothness;
                this.metallic = metallic;
            }

            // Such as LeatherJerkin, from the paint layer art/Paint/LeatherJerkin.
            public string Name
            {
                get { return name; }
            }

            // The body material it paints over, such as M_Body_Male.
            public Material Material
            {
                get { return material; }
            }

            // The paint in the material's UV layout, transparent where the skin shows.
            public Texture2D Texture
            {
                get { return texture; }
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

        // A garment's version for the body: its model, skinned to the body's armature; its visual
        // slot and layer; the slots it hides; the body faces it covers; the body face under each of
        // its triangles; and how far it moves visible body vertices inward.
        [Serializable]
        public sealed class Garment
        {
            [SerializeField] private string name;
            [SerializeField] private string slot;
            [SerializeField] private int layer;
            [SerializeField] private string[] hides;
            [SerializeField] private string hair;
            [SerializeField] private string cloth;
            [SerializeField] private GameObject model;
            [SerializeField] private int[] covered;
            [SerializeField] private int[] underlay;
            [SerializeField] private int[] pullInVertices;
            [SerializeField] private float[] pullInDistances;

            public Garment(string name, string slot, int layer, string[] hides, string hair, string cloth, GameObject model,
                int[] covered, int[] underlay, int[] pullInVertices, float[] pullInDistances)
            {
                this.name = name;
                this.slot = slot;
                this.layer = layer;
                this.hides = hides ?? Array.Empty<string>();
                this.hair = hair ?? string.Empty;
                this.cloth = cloth ?? string.Empty;
                this.model = model;
                this.covered = covered ?? Array.Empty<int>();
                this.underlay = underlay ?? Array.Empty<int>();
                this.pullInVertices = pullInVertices ?? Array.Empty<int>();
                this.pullInDistances = pullInDistances ?? Array.Empty<float>();
            }

            // Such as LeatherJerkin, from the garment art/Garments/LeatherJerkin.
            public string Name
            {
                get { return name; }
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

            // Such as Scarred, from the image T_Face_Male_Scarred.
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
