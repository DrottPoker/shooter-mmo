using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The looks a body model offers, from the approved exports of its art assets: one slot per
    // painted material that can show other images, such as the Face and the Eye, with its
    // options, the material's own image first, and the paint layers that fit the body, such as
    // the paint of a leather jerkin, in the order they are drawn. MMORPG > Tools > Art Pipeline >
    // Update Appearance Catalogs writes it from asset.json; see docs/ART_PIPELINE.md.
    public sealed class BodyAppearanceCatalog : ScriptableObject
    {
        [SerializeField] private Slot[] slots = Array.Empty<Slot>();
        [SerializeField] private Paint[] paints = Array.Empty<Paint>();

        public IReadOnlyList<Slot> Slots
        {
            get { return slots; }
        }

        // In the order they are drawn, the first one lowest.
        public IReadOnlyList<Paint> Paints
        {
            get { return paints; }
        }

        public void Configure(Slot[] catalogSlots, Paint[] catalogPaints = null)
        {
            slots = catalogSlots ?? Array.Empty<Slot>();
            paints = catalogPaints ?? Array.Empty<Paint>();
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
