using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // What a body looks like: for appearance slots of its catalog, such as Face or Eye, the
    // option it shows, the paint layers it wears, such as Tattoo, the garments it wears, each in
    // the colors of its dye channels or in its natural colors, and the attachments its sockets
    // hold, such as a sword. A slot the look does not name shows the material's own image. Looks
    // are values; equal looks have equal keys.
    public sealed class BodyLook : IEquatable<BodyLook>
    {
        // The key entry of the paint layers, Paint=A+B.
        public const string PaintKey = "Paint";

        // The key entry of the garments, Garment=A+B, where a dyed garment is followed by the
        // colors of its dye channels, such as WoolCloak@2B4364.
        public const string GarmentKey = "Garment";

        // The key entry of the attachments, Attachment=A+B.
        public const string AttachmentKey = "Attachment";

        private const char DyeSeparator = '@';
        private const char DyeColorSeparator = '.';

        private static readonly IReadOnlyList<Color> NoDyes = new Color[0];

        public static readonly BodyLook Default = new BodyLook(
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            new SortedSet<string>(StringComparer.Ordinal),
            new SortedSet<string>(StringComparer.Ordinal),
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            new SortedSet<string>(StringComparer.Ordinal));

        private readonly SortedDictionary<string, string> options;
        private readonly SortedSet<string> paints;
        private readonly SortedSet<string> garments;
        // The dye colors of the dyed garments, RRGGBB joined by dots, by garment.
        private readonly SortedDictionary<string, string> dyes;
        private readonly SortedSet<string> attachments;

        private BodyLook(SortedDictionary<string, string> options, SortedSet<string> paints, SortedSet<string> garments,
            SortedDictionary<string, string> dyes, SortedSet<string> attachments)
        {
            this.options = options;
            this.paints = paints;
            this.garments = garments;
            this.dyes = dyes;
            this.attachments = attachments;
            Key = KeyOf(options, paints, garments, dyes, attachments);
        }

        // Such as "Attachment=IronSword;Eye=Brown;Face=Clean;Garment=LeatherJerkin+WoolCloak@2B4364;Paint=Tattoo",
        // empty for the default look.
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

        // By name; each is held on its own socket.
        public IReadOnlyCollection<string> Attachments
        {
            get { return attachments; }
        }

        public bool HasAttachment(string attachment)
        {
            return attachment != null && attachments.Contains(attachment);
        }

        public bool HasPaint(string paint)
        {
            return paint != null && paints.Contains(paint);
        }

        public bool HasGarment(string garment)
        {
            return garment != null && garments.Contains(garment);
        }

        // The colors of a worn garment's dye channels in channel order, or none for its natural
        // colors.
        public IReadOnlyList<Color> DyesFor(string garment)
        {
            if (garment == null || !dyes.TryGetValue(garment, out var colors))
            {
                return NoDyes;
            }

            return colors.Split(DyeColorSeparator)
                .Select(color => ColorUtility.TryParseHtmlString("#" + color, out var parsed) ? parsed : Color.white)
                .ToArray();
        }

        // The look with or without one paint layer.
        public BodyLook WithPaint(string paint, bool worn)
        {
            if (!IsName(paint))
            {
                throw new ArgumentException("Paint layers are names of letters and digits.");
            }

            return new BodyLook(options, Toggled(paints, paint, worn), garments, dyes, attachments);
        }

        // The look with or without one garment, worn in its natural colors.
        public BodyLook WithGarment(string garment, bool worn)
        {
            return WithGarment(garment, worn, null);
        }

        // The look with one garment worn in the colors of its dye channels, "#RRGGBB" or "RRGGBB" in
        // channel order, or in its natural colors when there are none.
        public BodyLook WithGarment(string garment, IReadOnlyList<string> dyeColors)
        {
            return WithGarment(garment, true, dyeColors);
        }

        private BodyLook WithGarment(string garment, bool worn, IReadOnlyList<string> dyeColors)
        {
            if (!IsName(garment))
            {
                throw new ArgumentException("Garments are names of letters and digits.");
            }

            var colors = (dyeColors ?? Array.Empty<string>())
                .Select(color => color != null && color.StartsWith("#", StringComparison.Ordinal) ? color.Substring(1) : color)
                .ToArray();
            if (colors.Any(color => !IsHexColor(color)))
            {
                throw new ArgumentException("Dye colors are RRGGBB in hexadecimal digits.");
            }

            var changedDyes = new SortedDictionary<string, string>(dyes, StringComparer.Ordinal);
            changedDyes.Remove(garment);
            if (worn && colors.Length > 0)
            {
                changedDyes[garment] = string.Join(DyeColorSeparator.ToString(), colors.Select(color => color.ToUpperInvariant()));
            }

            return new BodyLook(options, paints, Toggled(garments, garment, worn), changedDyes, attachments);
        }

        // The look without its garments, such as the look a character chose, before its equipment
        // dresses it.
        public BodyLook WithoutGarments()
        {
            return new BodyLook(options, paints, new SortedSet<string>(StringComparer.Ordinal),
                new SortedDictionary<string, string>(StringComparer.Ordinal), attachments);
        }

        // The look with or without one attachment held on its socket.
        public BodyLook WithAttachment(string attachment, bool held)
        {
            if (!IsName(attachment))
            {
                throw new ArgumentException("Attachments are names of letters and digits.");
            }

            return new BodyLook(options, paints, garments, dyes, Toggled(attachments, attachment, held));
        }

        // The look holding nothing, such as the look a character chose, before its equipment fills
        // its hands.
        public BodyLook WithoutAttachments()
        {
            return new BodyLook(options, paints, garments, dyes, new SortedSet<string>(StringComparer.Ordinal));
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
            if (!IsName(slot) || slot == PaintKey || slot == GarmentKey || slot == AttachmentKey
                || (!string.IsNullOrEmpty(option) && !IsName(option)))
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

            return new BodyLook(changed, paints, garments, dyes, attachments);
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
                if (slot == PaintKey)
                {
                    foreach (var name in value.Split('+'))
                    {
                        if (IsName(name))
                        {
                            look = look.WithPaint(name, true);
                        }
                    }
                }
                else if (slot == AttachmentKey)
                {
                    foreach (var name in value.Split('+'))
                    {
                        if (IsName(name))
                        {
                            look = look.WithAttachment(name, true);
                        }
                    }
                }
                else if (slot == GarmentKey)
                {
                    foreach (var garment in value.Split('+'))
                    {
                        var parts = garment.Split(DyeSeparator);
                        var colors = parts.Length == 2 ? parts[1].Split(DyeColorSeparator) : Array.Empty<string>();
                        if (parts.Length <= 2 && IsName(parts[0]) && colors.All(IsHexColor))
                        {
                            look = look.WithGarment(parts[0], colors);
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

        private static bool IsHexColor(string text)
        {
            return text != null && text.Length == 6 && text.All(character =>
                (character >= '0' && character <= '9') || (character >= 'A' && character <= 'F') ||
                (character >= 'a' && character <= 'f'));
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
        private static string KeyOf(SortedDictionary<string, string> options, SortedSet<string> paints, SortedSet<string> garments,
            SortedDictionary<string, string> dyes, SortedSet<string> attachments)
        {
            var entries = new SortedDictionary<string, string>(options, StringComparer.Ordinal);
            if (attachments.Count > 0)
            {
                entries[AttachmentKey] = string.Join("+", attachments);
            }

            if (paints.Count > 0)
            {
                entries[PaintKey] = string.Join("+", paints);
            }

            if (garments.Count > 0)
            {
                entries[GarmentKey] = string.Join("+", garments.Select(garment =>
                    dyes.TryGetValue(garment, out var colors) ? garment + DyeSeparator + colors : garment));
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
