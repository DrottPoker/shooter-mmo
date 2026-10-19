using System;
using Mmorpg.Client.Items;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Combat;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Combat
{
    // What the client knows about its own loadout from the bundled item catalog and the
    // committed inventory. The worker resolves the loadout from GameService's equipment
    // summary and decides every attack; the client uses this only to walk into range and
    // to show the live arrow count.
    public static class CombatLoadoutRules
    {
        // The range of the weapon in the main hand slot, as the worker resolves it.
        public static bool TryGetWeaponRange(
            ClientItemCatalog catalog,
            CharacterInventorySnapshot snapshot,
            out float range)
        {
            range = 0f;
            if (catalog == null || snapshot == null || snapshot.Equipment == null)
            {
                return false;
            }

            for (var index = 0; index < snapshot.Equipment.Count; index++)
            {
                var slot = snapshot.Equipment[index];
                if (slot == null
                    || !string.Equals(slot.SlotId, ItemEquipmentSlotIds.MainHand, StringComparison.Ordinal))
                {
                    continue;
                }

                return slot.Item != null
                    && catalog.TryGetDefinition(slot.Item.DefinitionId, out var definition)
                    && TryGetWeaponRange(definition, out range);
            }

            return false;
        }

        public static bool TryGetWeaponRange(ItemDefinition definition, out float range)
        {
            range = 0f;
            var weapon = definition?.Weapon;
            if (weapon == null
                || string.IsNullOrEmpty(weapon.WeaponFamily)
                || !CombatContentRules.IsValidAttackRange(weapon.Range))
            {
                return false;
            }

            range = weapon.Range;
            return true;
        }

        // The arrow count to show for the equipped ammunition stack: the worker's live
        // count, which already subtracts shots that are not committed yet, while it names
        // this stack, and the committed quantity otherwise.
        public static int DisplayedAmmunitionQuantity(
            Guid equippedItemInstanceId,
            int committedQuantity,
            RealtimeOwnerCombatState owner)
        {
            return owner != null
                && owner.AmmunitionItemInstanceId != Guid.Empty
                && owner.AmmunitionItemInstanceId == equippedItemInstanceId
                    ? owner.AmmunitionQuantity
                    : committedQuantity;
        }
    }
}
