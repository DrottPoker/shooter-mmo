using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The looks a body model offers, from the approved exports of its art asset: one slot per
    // painted material that can show other images, such as the Face and the Eye, with its
    // options, the material's own image first. MMORPG > Tools > Art Pipeline > Update
    // Appearance Catalogs writes it from asset.json; see docs/ART_PIPELINE.md.
    public sealed class BodyAppearanceCatalog : ScriptableObject
    {
        [SerializeField] private Slot[] slots = Array.Empty<Slot>();

        public IReadOnlyList<Slot> Slots
        {
            get { return slots; }
        }

        public void Configure(Slot[] catalogSlots)
        {
            slots = catalogSlots ?? Array.Empty<Slot>();
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
