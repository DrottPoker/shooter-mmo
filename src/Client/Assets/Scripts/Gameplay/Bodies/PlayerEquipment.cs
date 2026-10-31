using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items.Presentation;

namespace Mmorpg.Client.Gameplay
{
    // The items the local player wears, as its inventory shows them with its predicted moves, the
    // item presentation that names their garments, and the equipment slot whose item the player
    // uses, such as tool while it gathers. The composition root keeps it up to date from the
    // inventory and the gathering, so the local body changes its garments as soon as an item moves
    // and holds its tool while it gathers.
    public sealed class PlayerEquipment
    {
        private static readonly EquippedItem[] NoItems = new EquippedItem[0];

        public event Action Changed;

        // In the order of the catalog's equipment slots.
        public IReadOnlyList<EquippedItem> Items { get; private set; } = NoItems;

        // The presentation of the bundled item catalog, or null before it is loaded.
        public ItemPresentationCatalogIndex Presentation { get; private set; }

        // The equipment slot whose item the player uses now, or empty.
        public string UsedSlot { get; private set; } = string.Empty;

        // The definition of the item in the used slot, or empty.
        public string ToolInUse
        {
            get
            {
                foreach (var item in Items)
                {
                    if (UsedSlot.Length > 0 && string.Equals(item.SlotId, UsedSlot, StringComparison.Ordinal))
                    {
                        return item.DefinitionId;
                    }
                }

                return string.Empty;
            }
        }

        public void Set(IReadOnlyList<EquippedItem> items, ItemPresentationCatalogIndex presentation)
        {
            var shown = items != null ? items.ToArray() : NoItems;
            if (presentation == Presentation && shown.SequenceEqual(Items))
            {
                return;
            }

            Items = shown;
            Presentation = presentation;
            Changed?.Invoke();
        }

        public void Use(string slotId)
        {
            var used = slotId ?? string.Empty;
            if (string.Equals(used, UsedSlot, StringComparison.Ordinal))
            {
                return;
            }

            UsedSlot = used;
            Changed?.Invoke();
        }
    }
}
