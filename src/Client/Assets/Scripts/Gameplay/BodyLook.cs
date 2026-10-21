using System;
using System.Collections.Generic;
using System.Text;

namespace Mmorpg.Client.Gameplay
{
    // What a body looks like: for appearance slots of its catalog, such as Face or Eye, the
    // option it shows. A slot the look does not name shows the material's own image. Looks are
    // values; equal looks have equal keys.
    public sealed class BodyLook : IEquatable<BodyLook>
    {
        public static readonly BodyLook Default = new BodyLook(new SortedDictionary<string, string>(StringComparer.Ordinal));

        private readonly SortedDictionary<string, string> options;

        private BodyLook(SortedDictionary<string, string> options)
        {
            this.options = options;
            Key = KeyOf(options);
        }

        // Such as "Eye=Brown;Face=Scarred", empty for the default look.
        public string Key { get; }

        // The option of the slot, or null for the material's own image.
        public string OptionFor(string slot)
        {
            return slot != null && options.TryGetValue(slot, out var option) ? option : null;
        }

        // The look with another option in one slot; null or empty restores the slot's own image.
        public BodyLook With(string slot, string option)
        {
            if (!IsName(slot) || (!string.IsNullOrEmpty(option) && !IsName(option)))
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

            return new BodyLook(changed);
        }

        // The look of a key, skipping entries that are not slot=option names.
        public static BodyLook Parse(string key)
        {
            var look = Default;
            foreach (var entry in (key ?? string.Empty).Split(';'))
            {
                var separator = entry.IndexOf('=');
                if (separator > 0
                    && IsName(entry.Substring(0, separator))
                    && IsName(entry.Substring(separator + 1)))
                {
                    look = look.With(entry.Substring(0, separator), entry.Substring(separator + 1));
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

        private static string KeyOf(SortedDictionary<string, string> options)
        {
            var key = new StringBuilder();
            foreach (var entry in options)
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
