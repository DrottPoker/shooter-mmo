using System;
using System.Collections.Generic;
using Mmorpg.WorldData.Items.Presentation;

namespace Mmorpg.Client.Gameplay
{
    // An item a character wears: its equipment slot, such as body, and its definition.
    public readonly struct EquippedItem : IEquatable<EquippedItem>
    {
        public EquippedItem(string slotId, string definitionId)
        {
            SlotId = slotId ?? string.Empty;
            DefinitionId = definitionId ?? string.Empty;
        }

        public string SlotId { get; }

        public string DefinitionId { get; }

        public bool Equals(EquippedItem other)
        {
            return string.Equals(SlotId, other.SlotId, StringComparison.Ordinal)
                && string.Equals(DefinitionId, other.DefinitionId, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is EquippedItem other && Equals(other);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(SlotId) * 31 + StringComparer.Ordinal.GetHashCode(DefinitionId);
        }
    }

    // Dresses a body in the garments of its equipped items, as the item presentation names them,
    // in the items' dye colors, and puts the attachments they name, such as a sword, on the sockets
    // of their equipment slots, or the tool the character uses in its hands. See
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    public static class EquipmentGarments
    {
        // The sockets of the hands, which a tool held with both hands empties.
        public static readonly string[] HandSockets = { "main_hand", "off_hand" };

        // The look a character chose, with its hair, dressed in the garments of its items in place
        // of every other garment it names. A lowered item, such as a hood pushed back, shows its
        // lowered garments when it has them. A body wears one garment per visual slot: the garment
        // of the item in the equipment slot of that name wins, such as a helmet in head over a robe's
        // hood, and otherwise the item earlier in the equipment. A garment the body's catalog lacks
        // is not worn. An item's attachment is held only on the socket of the equipment slot the item
        // is in, such as a sword in main_hand on Socket_MainHand, and only when the catalog has it.
        // A tool, such as a pickaxe, is held only while the character uses it: toolInUse names its
        // item, and its attachment takes the place of the one on its socket, and of both hands' for
        // a tool held with both hands.
        public static BodyLook Dress(
            BodyLook own,
            BodyAppearanceCatalog catalog,
            IReadOnlyList<EquippedItem> equipment,
            ItemPresentationCatalogIndex presentation,
            bool lowered,
            string toolInUse = null)
        {
            var look = own ?? BodyLook.Default;
            if (catalog == null)
            {
                return look;
            }

            var dressed = look.WithoutGarments().WithoutAttachments();
            foreach (var hair in look.Garments)
            {
                var garment = catalog.GarmentNamed(hair);
                if (garment != null && garment.Slot == BodyAppearanceCatalog.Garment.HairSlot)
                {
                    dressed = dressed.WithGarment(hair, true);
                }
            }

            if (presentation == null)
            {
                return dressed;
            }

            var tool = ToolNamed(catalog, presentation, toolInUse);
            if (tool != null)
            {
                dressed = dressed.WithAttachment(tool.Name, true);
            }

            if (equipment == null)
            {
                return dressed;
            }

            // By visual slot: the garment, its item's colors, and whether its item is in the slot.
            var chosen = new Dictionary<string, (string Name, string[] Dyes, bool OwnSlot)>(StringComparer.Ordinal);
            foreach (var item in equipment)
            {
                if (!presentation.TryGet(item.DefinitionId, out var entry))
                {
                    continue;
                }

                var attachment = catalog.AttachmentNamed(entry.attachment);
                if (attachment != null
                    && !attachment.IsTool
                    && string.Equals(attachment.Socket, item.SlotId, StringComparison.Ordinal)
                    && !TakenBy(tool, attachment))
                {
                    dressed = dressed.WithAttachment(attachment.Name, true);
                }

                if (entry.garments == null)
                {
                    continue;
                }

                var names = lowered && entry.loweredGarments != null && entry.loweredGarments.Length > 0
                    ? entry.loweredGarments
                    : entry.garments;
                foreach (var name in names)
                {
                    var garment = catalog.GarmentNamed(name);
                    if (garment == null || garment.Slot == BodyAppearanceCatalog.Garment.HairSlot)
                    {
                        continue;
                    }

                    var ownSlot = string.Equals(garment.Slot, item.SlotId, StringComparison.Ordinal);
                    if (!chosen.TryGetValue(garment.Slot, out var current) || (ownSlot && !current.OwnSlot))
                    {
                        chosen[garment.Slot] = (name, entry.dyes ?? Array.Empty<string>(), ownSlot);
                    }
                }
            }

            foreach (var garment in chosen.Values)
            {
                dressed = dressed.WithGarment(garment.Name, garment.Dyes);
            }

            return dressed;
        }

        // The tool attachment of the item a character uses, or null when it uses none, the item
        // names no attachment, or its attachment is no tool the catalog has.
        public static BodyAppearanceCatalog.Attachment ToolNamed(
            BodyAppearanceCatalog catalog,
            ItemPresentationCatalogIndex presentation,
            string toolInUse)
        {
            if (catalog == null || presentation == null || string.IsNullOrEmpty(toolInUse)
                || !presentation.TryGet(toolInUse, out var entry))
            {
                return null;
            }

            var attachment = catalog.AttachmentNamed(entry.attachment);
            return attachment != null && attachment.IsTool ? attachment : null;
        }

        // Whether a tool in use leaves no room for an attachment: it holds the attachment's socket,
        // or both hands while the attachment is held in a hand.
        private static bool TakenBy(BodyAppearanceCatalog.Attachment tool, BodyAppearanceCatalog.Attachment attachment)
        {
            if (tool == null)
            {
                return false;
            }

            return string.Equals(tool.Socket, attachment.Socket, StringComparison.Ordinal)
                || (tool.HoldsWithBothHands && Array.IndexOf(HandSockets, attachment.Socket) >= 0);
        }
    }
}
