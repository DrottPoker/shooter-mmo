using System;
using System.Collections.Generic;
using System.Text;

namespace Mmorpg.Client.Gameplay
{
    // What a body looks like: for appearance slots of its catalog, such as Face or Eye, the
    // option it shows, the paint layers it wears, such as LeatherJerkin, and the garments it
    // wears. A slot the look does not name shows the material's own image. Looks are values; equal
    // looks have equal keys.
    public sealed class BodyLook : IEquatable<BodyLook>
    {
        // The key entry of the paint layers, Paint=A+B.
        public const string PaintKey = "Paint";

        // The key entry of the garments, Garment=A+B.
        public const string GarmentKey = "Garment";

        public static readonly BodyLook Default = new BodyLook(
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            new SortedSet<string>(StringComparer.Ordinal),
            new SortedSet<string>(StringComparer.Ordinal));

        private readonly SortedDictionary<string, string> options;
        private readonly SortedSet<string> paints;
        private readonly SortedSet<string> garments;

        private BodyLook(SortedDictionary<string, string> options, SortedSet<string> paints, SortedSet<string> garments)
        {
            this.options = options;
            this.paints = paints;
            this.garments = garments;
            Key = KeyOf(options, paints, garments);
        }

        // Such as "Eye=Brown;Face=Scarred;Garment=LeatherJerkin;Paint=Tattoo", empty for the
        // default look.
        public string Key { get; }

        public IReadOnlyCollection<string> Paints
        {
            get { return paints; }
        }

        // By name; the catalog orders them by layer.
        public IReadOnlyCollection<string> Garments
        {
            get { return garments; }
        }

        public bool HasPaint(string paint)
        {
            return paint != null && paints.Contains(paint);
        }

        public bool HasGarment(string garment)
        {
            return garment != null && garments.Contains(garment);
        }

        // The look with or without one paint layer.
        public BodyLook WithPaint(string paint, bool worn)
        {
            if (!IsName(paint))
            {
                throw new ArgumentException("Paint layers are names of letters and digits.");
            }

            return new BodyLook(options, Toggled(paints, paint, worn), garments);
        }

        // The look with or without one garment.
        public BodyLook WithGarment(string garment, bool worn)
        {
            if (!IsName(garment))
            {
                throw new ArgumentException("Garments are names of letters and digits.");
            }

            return new BodyLook(options, paints, Toggled(garments, garment, worn));
        }

        private static SortedSet<string> Toggled(SortedSet<string> names, string name, bool worn)
        {
            var changed = new SortedSet<string>(names, StringComparer.Ordinal);
            if (worn)
            {
                changed.Add(name);
            }
            else
            {
                changed.Remove(name);
            }

            return changed;
        }

        // The option of the slot, or null for the material's own image.
        public string OptionFor(string slot)
        {
            return slot != null && options.TryGetValue(slot, out var option) ? option : null;
        }

        // The look with another option in one slot; null or empty restores the slot's own image.
        public BodyLook With(string slot, string option)
        {
            if (!IsName(slot) || slot == PaintKey || slot == GarmentKey || (!string.IsNullOrEmpty(option) && !IsName(option)))
            {
                throw new ArgumentException("Slots and options are names of letters and digits.");
            }

            var changed = new SortedDictionary<string, string>(options, StringComparer.Ordinal);
            if (string.IsNullOrEmpty(option))
            {
                changed.Remove(slot);
            }
            else
            {
                changed[slot] = option;
            }

            return new BodyLook(changed, paints, garments);
        }

        // The look of a key, skipping entries that are not slot=option names.
        public static BodyLook Parse(string key)
        {
            var look = Default;
            foreach (var entry in (key ?? string.Empty).Split(';'))
            {
                var separator = entry.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var slot = entry.Substring(0, separator);
                var value = entry.Substring(separator + 1);
                if (slot == PaintKey || slot == GarmentKey)
                {
                    foreach (var name in value.Split('+'))
                    {
                        if (IsName(name))
                        {
                            look = slot == PaintKey ? look.WithPaint(name, true) : look.WithGarment(name, true);
                        }
                    }
                }
                else if (IsName(slot) && IsName(value))
                {
                    look = look.With(slot, value);
                }
            }

            return look;
        }

        // A look picked from the catalog by a stable hash of the seed, such as the persistent
        // id of a development bot, so a crowd shows every option and each bot keeps its look.
        public static BodyLook Varied(string seed, BodyAppearanceCatalog catalog)
        {
            var look = Default;
            if (catalog == null)
            {
                return look;
            }

            foreach (var slot in catalog.Slots)
            {
                if (slot == null || slot.Options.Count == 0)
                {
                    continue;
                }

                var option = slot.Options[(int)(Mix(StableHash(seed + "/" + slot.Name)) % (uint)slot.Options.Count)];
                look = look.With(slot.Name, option.Name);
            }

            return look;
        }

        // The finalizer of MurmurHash3: every input bit reaches every output bit. The low bits of
        // FNV-1a only depend on the low bits of the characters, so the slots of one seed would
        // otherwise pick alike.
        public static uint Mix(uint hash)
        {
            hash ^= hash >> 16;
            hash *= 0x85EBCA6Bu;
            hash ^= hash >> 13;
            hash *= 0xC2B2AE35u;
            hash ^= hash >> 16;
            return hash;
        }

        // FNV-1a over the UTF-16 code units, the same in every process.
        public static uint StableHash(string text)
        {
            var hash = 2166136261u;
            foreach (var character in text ?? string.Empty)
            {
                hash = (hash ^ character) * 16777619u;
            }

            return hash;
        }

        public bool Equals(BodyLook other)
        {
            return other != null && string.Equals(Key, other.Key, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as BodyLook);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(Key);
        }

        public override string ToString()
        {
            return Key;
        }

        private static bool IsName(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var character in text)
            {
                if (!char.IsLetterOrDigit(character) || character > 127)
                {
                    return false;
                }
            }

            return true;
        }

        // The entries in the ordinal order of their names, so Garment comes before Paint.
        private static string KeyOf(SortedDictionary<string, string> options, SortedSet<string> paints, SortedSet<string> garments)
        {
            var entries = new SortedDictionary<string, string>(options, StringComparer.Ordinal);
            if (paints.Count > 0)
            {
                entries[PaintKey] = string.Join("+", paints);
            }

            if (garments.Count > 0)
            {
                entries[GarmentKey] = string.Join("+", garments);
            }

            var key = new StringBuilder();
            foreach (var entry in entries)
            {
                if (key.Length > 0)
                {
                    key.Append(';');
                }

                key.Append(entry.Key).Append('=').Append(entry.Value);
            }

            return key.ToString();
        }
    }
}
