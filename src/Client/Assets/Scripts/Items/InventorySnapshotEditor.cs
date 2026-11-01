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
        private readonly RecoveryStorageInventory recoverySource;
        private readonly List<EditableContainer> containers = new List<EditableContainer>();
        private readonly InventoryItem[] equipment;
        private readonly List<RecoveryDelivery> deliveries;
        private EditableContainer bagContents;
        private long recoveryRevision;
        private bool recoveryChanged;

        public InventorySnapshotEditor(
            CharacterInventorySnapshot snapshot,
            InventoryContainer bank,
            RecoveryStorageInventory recovery)
        {
            source = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            recoverySource = recovery ?? snapshot.RecoveryStorage;
            containers.Add(new EditableContainer(snapshot.PermanentInventory));
            containers.Add(new EditableContainer(bank ?? snapshot.Bank));
            if (snapshot.EquippedBag != null)
            {
                bagContents = new EditableContainer(snapshot.EquippedBag.Contents);
                containers.Add(bagContents);
            }

            equipment = snapshot.Equipment.Select(slot => slot.Item).ToArray();
            deliveries = recoverySource?.Deliveries.ToList() ?? new List<RecoveryDelivery>();
            recoveryRevision = recoverySource?.Revision ?? 0;
        }

        public Guid RecoveryStorageContainerId
        {
            get { return recoverySource?.ContainerId ?? Guid.Empty; }
        }

        public InventoryItem EquippedBag
        {
            get
            {
                var index = EquipmentIndex(ItemEquipmentSlotIds.Bag);
                return index < 0 ? null : equipment[index];
            }
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

        public bool TryFindDelivery(Guid itemInstanceId, out RecoveryDelivery delivery)
        {
            delivery = deliveries.FirstOrDefault(candidate => candidate.Items.Any(item =>
                item.Item.ItemInstanceId == itemInstanceId));
            return delivery != null;
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

        // Takes the item out of its slot. A Bag that leaves the Bag slot takes its contents
        // container out of view with it.
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
                    if (string.Equals(source.Equipment[index].SlotId, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal)
                        && bagContents != null)
                    {
                        containers.Remove(bagContents);
                        bagContents = null;
                    }

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
            if (index < 0
                || item == null
                || equipment[index] != null
                || string.Equals(equipmentSlotId, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal))
            {
                return false;
            }

            equipment[index] = item;
            return true;
        }

        // Equips a Bag together with its contents container, which becomes visible.
        public bool TryEquipBag(InventoryItem bag, InventoryContainer contents)
        {
            var index = EquipmentIndex(ItemEquipmentSlotIds.Bag);
            if (index < 0 || bag == null || contents == null || equipment[index] != null || bagContents != null)
            {
                return false;
            }

            equipment[index] = bag;
            bagContents = new EditableContainer(contents) { Changed = true };
            containers.Add(bagContents);
            return true;
        }

        public bool TryGetSlotItem(Guid containerId, int slotIndex, out InventoryItem item)
        {
            item = null;
            var container = FindContainer(containerId);
            return container != null && container.Items.TryGetValue(slotIndex, out item);
        }

        public InventoryItem GetEquipped(string equipmentSlotId)
        {
            var index = EquipmentIndex(equipmentSlotId);
            return index < 0 ? null : equipment[index];
        }

        public bool HasEquipmentSlot(string equipmentSlotId)
        {
            return EquipmentIndex(equipmentSlotId) >= 0;
        }

        public bool SetContainerRevision(Guid containerId, long revision)
        {
            if (containerId != Guid.Empty && containerId == RecoveryStorageContainerId)
            {
                recoveryRevision = revision;
                recoveryChanged = true;
                return true;
            }

            var container = FindContainer(containerId);
            if (container == null)
            {
                return false;
            }

            container.Revision = revision;
            return true;
        }

        // Takes a claimed delivery out of Recovery Storage. Its items leave with it, so the
        // caller places every one of them elsewhere.
        public RecoveryDelivery RemoveDelivery(Guid deliveryId)
        {
            var delivery = deliveries.FirstOrDefault(candidate => candidate.DeliveryId == deliveryId);
            if (delivery != null)
            {
                deliveries.Remove(delivery);
                recoveryChanged = true;
            }

            return delivery;
        }

        // A known container whose contents changed without a new revision would leave the
        // patched snapshot incoherent with the server's.
        public bool ChangedContainersHaveRevisions(ISet<Guid> revisedContainerIds)
        {
            return containers.All(container =>
                    !container.Changed || revisedContainerIds.Contains(container.Source.ContainerId))
                && (!recoveryChanged || revisedContainerIds.Contains(RecoveryStorageContainerId));
        }

        public CharacterInventorySnapshot Build(
            long itemStateRevision,
            long carriedWeight,
            long carryCapacity,
            long currency)
        {
            var permanent = containers[0].Build();
            var bank = containers[1].Build();
            var equipmentSlots = new InventoryEquipmentSlot[equipment.Length];
            for (var index = 0; index < equipment.Length; index++)
            {
                var slot = source.Equipment[index];
                equipmentSlots[index] = ReferenceEquals(slot.Item, equipment[index])
                    ? slot
                    : new InventoryEquipmentSlot(slot.SlotId, slot.SortOrder, equipment[index]);
            }

            var bagItem = EquippedBag;
            if ((bagItem == null) != (bagContents == null))
            {
                throw new InvalidOperationException("An equipped Bag must have its contents container.");
            }

            EquippedBagInventory equippedBag = null;
            if (bagItem != null)
            {
                var contents = bagContents.Build();
                equippedBag = source.EquippedBag != null
                    && ReferenceEquals(bagItem, source.EquippedBag.Item)
                    && ReferenceEquals(contents, source.EquippedBag.Contents)
                        ? source.EquippedBag
                        : new EquippedBagInventory(bagItem, contents);
            }

            return new CharacterInventorySnapshot(
                source.CharacterId,
                source.CatalogRevision,
                itemStateRevision,
                permanent,
                equipmentSlots,
                equippedBag,
                bank,
                BuildRecovery(),
                carriedWeight,
                carryCapacity,
                InventorySnapshotMapper.CalculateLoadRatioBasisPoints(carriedWeight, carryCapacity),
                EncumbranceRules.CalculateMovementMultiplierBasisPoints(carriedWeight, carryCapacity),
                currency);
        }

        // The empty contents container of a Bag, laid out as its catalog definition and as
        // GameService creates it.
        public static InventoryContainer CreateBagContents(ItemDefinition bag, Guid containerId, long revision)
        {
            var slots = bag.Bag.Slots
                .OrderBy(slot => slot.Index)
                .Select(slot => new InventorySlot(
                    slot.Index,
                    string.IsNullOrWhiteSpace(slot.Kind) ? BagSlotKindIds.General : slot.Kind,
                    slot.AcceptedTags ?? Array.Empty<string>(),
                    null))
                .ToArray();
            return new InventoryContainer(
                containerId,
                ItemContainerTypeIds.BagContents,
                revision,
                slots.Length,
                slots);
        }

        private RecoveryStorageInventory BuildRecovery()
        {
            if (recoverySource == null || !recoveryChanged)
            {
                return recoverySource;
            }

            return new RecoveryStorageInventory(recoverySource.ContainerId, recoveryRevision, deliveries.ToArray());
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
