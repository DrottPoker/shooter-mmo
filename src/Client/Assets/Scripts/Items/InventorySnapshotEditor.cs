using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    // A working copy of an inventory snapshot that items move through. The item state patch
    // and the operation predictor edit it and build a new immutable snapshot; items, slots,
    // and containers that did not change keep their instances.
    internal sealed class InventorySnapshotEditor
    {
        private readonly CharacterInventorySnapshot source;
        private readonly RecoveryStorageInventory recoveryStorage;
        private readonly List<EditableContainer> containers = new List<EditableContainer>();
        private readonly InventoryItem[] equipment;

        public InventorySnapshotEditor(
            CharacterInventorySnapshot snapshot,
            InventoryContainer bank,
            RecoveryStorageInventory recovery)
        {
            source = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            recoveryStorage = recovery ?? snapshot.RecoveryStorage;
            containers.Add(new EditableContainer(snapshot.PermanentInventory));
            containers.Add(new EditableContainer(bank ?? snapshot.Bank));
            containers.Add(new EditableContainer(snapshot.SecureContainer.Contents));
            if (snapshot.EquippedBag != null)
            {
                containers.Add(new EditableContainer(snapshot.EquippedBag.Contents));
            }

            equipment = snapshot.Equipment.Select(slot => slot.Item).ToArray();
        }

        public Guid RecoveryStorageContainerId
        {
            get { return recoveryStorage?.ContainerId ?? Guid.Empty; }
        }

        public bool HasContainer(Guid containerId)
        {
            return FindContainer(containerId) != null;
        }

        public bool TryGetContainerType(Guid containerId, out string containerType)
        {
            var container = FindContainer(containerId);
            containerType = container?.Source.ContainerType ?? string.Empty;
            return container != null;
        }

        public bool IsInRecoveryStorage(Guid itemInstanceId)
        {
            return recoveryStorage != null
                && recoveryStorage.Deliveries.Any(delivery => delivery.Items.Any(item =>
                    item.Item.ItemInstanceId == itemInstanceId));
        }

        public bool TryFind(Guid itemInstanceId, out InventoryItem item, out InventoryItemLocation location)
        {
            foreach (var container in containers)
            {
                foreach (var entry in container.Items)
                {
                    if (entry.Value.ItemInstanceId == itemInstanceId)
                    {
                        item = entry.Value;
                        location = new InventoryItemLocation(
                            InventoryItemLocationKind.Container,
                            container.Source.ContainerId,
                            container.Source.ContainerType,
                            entry.Key,
                            string.Empty,
                            Guid.Empty);
                        return true;
                    }
                }
            }

            for (var index = 0; index < equipment.Length; index++)
            {
                if (equipment[index] != null && equipment[index].ItemInstanceId == itemInstanceId)
                {
                    item = equipment[index];
                    location = InventoryItemLocation.ForEquipment(source.Equipment[index].SlotId);
                    return true;
                }
            }

            item = null;
            location = null;
            return false;
        }

        public InventoryItem Remove(Guid itemInstanceId)
        {
            foreach (var container in containers)
            {
                foreach (var entry in container.Items)
                {
                    if (entry.Value.ItemInstanceId == itemInstanceId)
                    {
                        container.Items.Remove(entry.Key);
                        container.Changed = true;
                        return entry.Value;
                    }
                }
            }

            for (var index = 0; index < equipment.Length; index++)
            {
                if (equipment[index] != null && equipment[index].ItemInstanceId == itemInstanceId)
                {
                    var removed = equipment[index];
                    equipment[index] = null;
                    return removed;
                }
            }

            return null;
        }

        // Puts the item into an existing, empty slot of a known container.
        public bool TryPlace(Guid containerId, int slotIndex, InventoryItem item)
        {
            var container = FindContainer(containerId);
            if (container == null
                || item == null
                || !container.Source.TryGetSlot(slotIndex, out _)
                || container.Items.ContainsKey(slotIndex))
            {
                return false;
            }

            container.Items[slotIndex] = item;
            container.Changed = true;
            return true;
        }

        // Replaces the item in an occupied slot, such as a stack whose quantity changed.
        public bool TryReplace(Guid itemInstanceId, InventoryItem replacement)
        {
            foreach (var container in containers)
            {
                foreach (var entry in container.Items)
                {
                    if (entry.Value.ItemInstanceId == itemInstanceId)
                    {
                        container.Items[entry.Key] = replacement;
                        container.Changed = true;
                        return true;
                    }
                }
            }

            for (var index = 0; index < equipment.Length; index++)
            {
                if (equipment[index] != null && equipment[index].ItemInstanceId == itemInstanceId)
                {
                    equipment[index] = replacement;
                    return true;
                }
            }

            return false;
        }

        public bool TryEquip(string equipmentSlotId, InventoryItem item)
        {
            var index = EquipmentIndex(equipmentSlotId);
            if (index < 0 || item == null || equipment[index] != null)
            {
                return false;
            }

            equipment[index] = item;
            return true;
        }

        public bool HasEquipmentSlot(string equipmentSlotId)
        {
            return EquipmentIndex(equipmentSlotId) >= 0;
        }

        public bool SetContainerRevision(Guid containerId, long revision)
        {
            var container = FindContainer(containerId);
            if (container == null)
            {
                return false;
            }

            container.Revision = revision;
            return true;
        }

        // A known container whose contents changed without a new revision would leave the
        // patched snapshot incoherent with the server's.
        public bool ChangedContainersHaveRevisions(ISet<Guid> revisedContainerIds)
        {
            return containers.All(container =>
                !container.Changed || revisedContainerIds.Contains(container.Source.ContainerId));
        }

        public CharacterInventorySnapshot Build(
            long itemStateRevision,
            long carriedWeight,
            long carryCapacity,
            long currency)
        {
            var built = containers.Select(container => container.Build()).ToArray();
            var equipmentSlots = new InventoryEquipmentSlot[equipment.Length];
            for (var index = 0; index < equipment.Length; index++)
            {
                var slot = source.Equipment[index];
                equipmentSlots[index] = ReferenceEquals(slot.Item, equipment[index])
                    ? slot
                    : new InventoryEquipmentSlot(slot.SlotId, slot.SortOrder, equipment[index]);
            }

            var bagIndex = EquipmentIndex(ItemEquipmentSlotIds.Bag);
            var equippedBag = source.EquippedBag;
            if (equippedBag != null)
            {
                var bagItem = bagIndex < 0 ? null : equipment[bagIndex];
                if (bagItem == null || bagItem.ItemInstanceId != equippedBag.Item.ItemInstanceId)
                {
                    throw new InvalidOperationException("The equipped Bag cannot change through an item edit.");
                }

                equippedBag = ReferenceEquals(bagItem, equippedBag.Item) && ReferenceEquals(built[3], equippedBag.Contents)
                    ? equippedBag
                    : new EquippedBagInventory(bagItem, built[3]);
            }

            return new CharacterInventorySnapshot(
                source.CharacterId,
                source.CatalogRevision,
                itemStateRevision,
                built[0],
                equipmentSlots,
                equippedBag,
                built[1],
                ReferenceEquals(built[2], source.SecureContainer.Contents)
                    ? source.SecureContainer
                    : new SecureContainerInventory(
                        source.SecureContainer.TierId,
                        source.SecureContainer.EntitlementRevision,
                        built[2]),
                recoveryStorage,
                carriedWeight,
                carryCapacity,
                InventorySnapshotMapper.CalculateLoadRatioBasisPoints(carriedWeight, carryCapacity),
                EncumbranceRules.CalculateMovementMultiplierBasisPoints(carriedWeight, carryCapacity),
                currency);
        }

        private EditableContainer FindContainer(Guid containerId)
        {
            return containerId == Guid.Empty
                ? null
                : containers.FirstOrDefault(container => container.Source.ContainerId == containerId);
        }

        private int EquipmentIndex(string equipmentSlotId)
        {
            for (var index = 0; index < source.Equipment.Count; index++)
            {
                if (string.Equals(source.Equipment[index].SlotId, equipmentSlotId, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        private sealed class EditableContainer
        {
            public EditableContainer(InventoryContainer container)
            {
                Source = container ?? throw new ArgumentNullException(nameof(container));
                Revision = container.Revision;
                foreach (var slot in container.Slots)
                {
                    if (slot.Item != null)
                    {
                        Items[slot.SlotIndex] = slot.Item;
                    }
                }
            }

            public InventoryContainer Source { get; }

            public Dictionary<int, InventoryItem> Items { get; } = new Dictionary<int, InventoryItem>();

            public long Revision { get; set; }

            public bool Changed { get; set; }

            public InventoryContainer Build()
            {
                if (!Changed && Revision == Source.Revision)
                {
                    return Source;
                }

                return new InventoryContainer(
                    Source.ContainerId,
                    Source.ContainerType,
                    Revision,
                    Source.SlotCapacity,
                    Source.Slots
                        .Select(slot =>
                        {
                            Items.TryGetValue(slot.SlotIndex, out var item);
                            return ReferenceEquals(item, slot.Item)
                                ? slot
                                : new InventorySlot(
                                    slot.SlotIndex,
                                    slot.SlotKind,
                                    slot.AcceptedTags.ToArray(),
                                    item);
                        })
                        .ToArray());
            }
        }
    }
}
