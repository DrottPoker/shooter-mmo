using System;

namespace Mmorpg.WorldData.Items
{
    // Container types of item custody. GameService persists them, and SimulationWorker and
    // Unity read them from snapshots.
    public static class ItemContainerTypeIds
    {
        public const string PermanentInventory = "permanent_inventory";
        public const string BagContents = "bag_contents";
        public const string Bank = "bank";
        public const string RecoveryStorage = "recovery_storage";
        public const string CorpseInventory = "corpse_inventory";
        public const string CorpseEquipment = "corpse_equipment";
        public const string CorpseBagContents = "corpse_bag_contents";

        // Carried containers travel with the character and count toward carried weight.
        public static bool IsCarried(string containerType)
        {
            return string.Equals(containerType, PermanentInventory, StringComparison.Ordinal)
                || string.Equals(containerType, BagContents, StringComparison.Ordinal);
        }

        public static bool IsCorpse(string containerType)
        {
            return string.Equals(containerType, CorpseInventory, StringComparison.Ordinal)
                || string.Equals(containerType, CorpseEquipment, StringComparison.Ordinal)
                || string.Equals(containerType, CorpseBagContents, StringComparison.Ordinal);
        }
    }

    // The three sections of a corpse view and the container type that holds each.
    public static class CorpseSectionKindIds
    {
        public const string GeneralInventory = "general_inventory";
        public const string Equipment = "equipment";
        public const string Bag = "bag";

        public static bool TryGetContainerType(string sectionKind, out string containerType)
        {
            switch (sectionKind)
            {
                case GeneralInventory:
                    containerType = ItemContainerTypeIds.CorpseInventory;
                    return true;
                case Equipment:
                    containerType = ItemContainerTypeIds.CorpseEquipment;
                    return true;
                case Bag:
                    containerType = ItemContainerTypeIds.CorpseBagContents;
                    return true;
                default:
                    containerType = string.Empty;
                    return false;
            }
        }
    }

    public static class BagDestinationRules
    {
        // Maps a container slot to the Bag destination kind that BagLocationRules judges. An
        // unknown container type has no destination kind, so callers reject it.
        public static bool TryResolve(
            string containerType,
            string slotKind,
            out BagDestinationKind destination)
        {
            switch (containerType)
            {
                case ItemContainerTypeIds.PermanentInventory:
                    destination = BagDestinationKind.PermanentInventoryGeneralSlot;
                    return true;
                case ItemContainerTypeIds.Bank:
                    destination = BagDestinationKind.BankGeneralSlot;
                    return true;
                case ItemContainerTypeIds.BagContents:
                    destination = string.Equals(slotKind, BagSlotKindIds.General, StringComparison.Ordinal)
                        ? BagDestinationKind.BagGeneralSlot
                        : BagDestinationKind.BagSpecializedSlot;
                    return true;
                case ItemContainerTypeIds.RecoveryStorage:
                    destination = BagDestinationKind.RecoveryStorageSlot;
                    return true;
                case ItemContainerTypeIds.CorpseInventory:
                case ItemContainerTypeIds.CorpseEquipment:
                case ItemContainerTypeIds.CorpseBagContents:
                    destination = BagDestinationKind.CorpseStorageSlot;
                    return true;
                default:
                    destination = default;
                    return false;
            }
        }
    }
}
