using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items.Presentation;

namespace Mmorpg.Client.Gameplay
{
    // The items the local player wears, as its inventory shows them with its predicted moves, and
    // the item presentation that names their garments. The composition root keeps it up to date
    // from the inventory, so the local body changes its garments as soon as an item moves.
    public sealed class PlayerEquipment
    {
        private static readonly EquippedItem[] NoItems = new EquippedItem[0];

        public event Action Changed;

        // In the order of the catalog's equipment slots.
        public IReadOnlyList<EquippedItem> Items { get; private set; } = NoItems;

        // The presentation of the bundled item catalog, or null before it is loaded.
        public ItemPresentationCatalogIndex Presentation { get; private set; }

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
    }
}
