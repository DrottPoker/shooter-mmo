using System;

namespace Mmorpg.GameProtocol
{
    // Cross-field rules of the Corpses family. The schema in Schema~ describes the
    // messages, and the generator writes their models and codecs.
    public static partial class RealtimeProtocol
    {
        // A bag swap exchanges two different bags with two different contents containers.
        private static bool SatisfiesRules(RealtimeCorpseInteractionIntent intent)
        {
            return intent.OperationKind != RealtimeCorpseInteractionKind.SwapBag
                || (intent.ItemInstanceId != intent.PlayerBagItemInstanceId
                    && intent.CorpseBagContentsContainerId != intent.PlayerBagContentsContainerId);
        }

        // Only a failed operation can leave the corpse view stale.
        private static bool SatisfiesRules(RealtimeCorpseInteractionResult result)
        {
            return !result.Succeeded || !result.RequiresCorpseRefresh;
        }

        // An item without bag contents has no contents revision.
        private static bool SatisfiesRules(RealtimeCorpseItem item)
        {
            return item.BagContentsContainerId != Guid.Empty || item.BagContentsRevision == 0;
        }

        // A snapshot replaces its section from nothing, a delta never goes back in revision,
        // every slot lies inside its section, and only equipment slots name an equipment slot.
        private static bool SatisfiesRules(RealtimeCorpseViewStateChunk chunk)
        {
            if (chunk.Revision < chunk.BaseRevision
                || (chunk.UpdateKind == RealtimeCorpseViewUpdateKind.Snapshot
                    && (chunk.BaseRevision != -1 || !chunk.ReplaceSection)))
            {
                return false;
            }

            var isEquipment = chunk.SectionKind == "equipment";
            foreach (var slot in chunk.Slots)
            {
                if (slot.SlotIndex >= chunk.SectionSlotCapacity
                    || isEquipment == string.IsNullOrWhiteSpace(slot.EquipmentSlotId))
                {
                    return false;
                }
            }

            return true;
        }
    }

    // Shorter constructors for slots outside the equipment section.
    public sealed partial class RealtimeCorpseSlot
    {
        public RealtimeCorpseSlot(int slotIndex, string slotKind, RealtimeCorpseItem item)
            : this(slotIndex, slotKind, Array.Empty<string>(), string.Empty, item)
        {
        }

        public RealtimeCorpseSlot(
            int slotIndex,
            string slotKind,
            string[] acceptedTags,
            RealtimeCorpseItem item)
            : this(slotIndex, slotKind, acceptedTags, string.Empty, item)
        {
        }
    }
}
