using System;
using System.Collections.Generic;
using System.Linq;

namespace Mmorpg.WorldData.Items
{
    // A stack that loot can top up. Two stacks merge when they hold the same definition, the
    // definition stacks, and their merge keys are equal; a null key never merges. The key
    // stands for the policies and the stack state that GameService compares.
    public sealed class LootAllStack
    {
        public LootAllStack(Guid itemInstanceId, string definitionId, int quantity, string mergeKey)
        {
            ItemInstanceId = itemInstanceId;
            DefinitionId = definitionId ?? string.Empty;
            Quantity = quantity;
            MergeKey = mergeKey;
        }

        public Guid ItemInstanceId { get; }

        public string DefinitionId { get; }

        public int Quantity { get; }

        public string MergeKey { get; }
    }

    // One equipment slot of the looter, in catalog order, with the item it holds.
    public sealed class LootAllEquipmentSlot
    {
        public LootAllEquipmentSlot(ItemEquipmentSlotEntry slot, LootAllStack occupant)
        {
            Slot = slot ?? throw new ArgumentNullException(nameof(slot));
            Occupant = occupant;
        }

        public ItemEquipmentSlotEntry Slot { get; }

        public LootAllStack Occupant { get; }
    }

    // One slot of carried storage: the Permanent Inventory, the equipped Bag, or the contents of
    // a corpse Bag that the looter equips.
    public sealed class LootAllCarriedSlot
    {
        public LootAllCarriedSlot(
            Guid containerId,
            string containerType,
            int slotIndex,
            string slotKind,
            IEnumerable<string> acceptedTags,
            LootAllStack occupant)
        {
            ContainerId = containerId;
            ContainerType = containerType ?? string.Empty;
            SlotIndex = slotIndex;
            SlotKind = slotKind ?? string.Empty;
            AcceptedTags = acceptedTags?.ToArray() ?? Array.Empty<string>();
            Occupant = occupant;
        }

        public Guid ContainerId { get; }

        public string ContainerType { get; }

        public int SlotIndex { get; }

        public string SlotKind { get; }

        public IReadOnlyList<string> AcceptedTags { get; }

        public LootAllStack Occupant { get; }
    }

    // A corpse Bag with the slots of its contents container. Only the corpse's equipped Bag
    // can hold items; its contents are also listed as corpse items of the Bag section.
    public sealed class LootAllBag
    {
        public LootAllBag(long carryCapacityBonus, IEnumerable<LootAllCarriedSlot> contents)
        {
            CarryCapacityBonus = carryCapacityBonus;
            Contents = contents?.ToArray() ?? Array.Empty<LootAllCarriedSlot>();
        }

        public long CarryCapacityBonus { get; }

        public IReadOnlyList<LootAllCarriedSlot> Contents { get; }

        public bool HasContents
        {
            get { return Contents.Any(slot => slot.Occupant != null); }
        }
    }

    public sealed class LootAllCorpseItem
    {
        public LootAllCorpseItem(
            Guid itemInstanceId,
            string definitionId,
            int quantity,
            string sectionKind,
            string mergeKey,
            bool canTake,
            LootAllBag bag = null)
        {
            ItemInstanceId = itemInstanceId;
            DefinitionId = definitionId ?? string.Empty;
            Quantity = quantity;
            SectionKind = sectionKind ?? string.Empty;
            MergeKey = mergeKey;
            CanTake = canTake;
            Bag = bag;
        }

        public Guid ItemInstanceId { get; }

        public string DefinitionId { get; }

        public int Quantity { get; }

        public string SectionKind { get; }

        public string MergeKey { get; }

        // False for an item that a policy keeps from changing its owner.
        public bool CanTake { get; }

        // Set for every Bag.
        public LootAllBag Bag { get; }
    }

    public sealed class LootAllRequest
    {
        public LootAllRequest(
            Func<string, ItemDefinition> definitions,
            Func<ItemDefinition, bool> meetsLevelRequirement,
            long carriedWeight,
            long carryCapacity,
            IEnumerable<LootAllEquipmentSlot> equipment,
            IEnumerable<LootAllCarriedSlot> permanentInventory,
            IEnumerable<LootAllCarriedSlot> bagContents,
            IEnumerable<LootAllCorpseItem> corpseItems,
            bool wholeItemsOnly)
        {
            Definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
            MeetsLevelRequirement = meetsLevelRequirement
                ?? throw new ArgumentNullException(nameof(meetsLevelRequirement));
            CarriedWeight = carriedWeight;
            CarryCapacity = carryCapacity;
            Equipment = equipment?.ToArray() ?? Array.Empty<LootAllEquipmentSlot>();
            PermanentInventory = permanentInventory?.ToArray() ?? Array.Empty<LootAllCarriedSlot>();
            BagContents = bagContents?.ToArray() ?? Array.Empty<LootAllCarriedSlot>();
            CorpseItems = corpseItems?.ToArray() ?? Array.Empty<LootAllCorpseItem>();
            WholeItemsOnly = wholeItemsOnly;
        }

        // Resolves a definition id, or returns null for an unknown one.
        public Func<string, ItemDefinition> Definitions { get; }

        public Func<ItemDefinition, bool> MeetsLevelRequirement { get; }

        public long CarriedWeight { get; }

        public long CarryCapacity { get; }

        public IReadOnlyList<LootAllEquipmentSlot> Equipment { get; }

        public IReadOnlyList<LootAllCarriedSlot> PermanentInventory { get; }

        public IReadOnlyList<LootAllCarriedSlot> BagContents { get; }

        // In slot order within each section.
        public IReadOnlyList<LootAllCorpseItem> CorpseItems { get; }

        // A live Mob corpse cannot keep part of a stack, so every step takes a whole item.
        public bool WholeItemsOnly { get; }
    }

    public enum LootAllStepKind
    {
        // A whole Bag, with its contents, takes the empty Bag slot.
        EquipBag = 1,

        // A whole item takes an empty equipment slot.
        Equip = 2,

        // A quantity tops up the equipped stack of the same item.
        TopUpEquipped = 3,

        // A quantity tops up a carried stack.
        Merge = 4,

        // A quantity takes an empty carried slot.
        Place = 5
    }

    public sealed class LootAllStep
    {
        internal LootAllStep(
            LootAllStepKind kind,
            LootAllCorpseItem item,
            int quantity,
            bool takesRest,
            string equipmentSlotId,
            Guid targetItemInstanceId,
            LootAllCarriedSlot destination)
        {
            Kind = kind;
            Item = item;
            Quantity = quantity;
            TakesRest = takesRest;
            EquipmentSlotId = equipmentSlotId ?? string.Empty;
            TargetItemInstanceId = targetItemInstanceId;
            Destination = destination;
        }

        public LootAllStepKind Kind { get; }

        public LootAllCorpseItem Item { get; }

        public int Quantity { get; }

        // True when the step takes all that is left of the corpse item, so the item itself
        // leaves the corpse; false when part of the stack stays behind.
        public bool TakesRest { get; }

        // The equipment slot of EquipBag, Equip, and TopUpEquipped.
        public string EquipmentSlotId { get; }

        // The stack that TopUpEquipped and Merge add to.
        public Guid TargetItemInstanceId { get; }

        // The empty slot that Place fills.
        public LootAllCarriedSlot Destination { get; }
    }

    public sealed class LootAllPlan
    {
        internal LootAllPlan(IReadOnlyList<LootAllStep> steps, long carriedWeight, long carryCapacity)
        {
            Steps = steps;
            CarriedWeight = carriedWeight;
            CarryCapacity = carryCapacity;
        }

        public IReadOnlyList<LootAllStep> Steps { get; }

        // The carry state after every step.
        public long CarriedWeight { get; }

        public long CarryCapacity { get; }
    }

    // Plans Loot all: everything that fits leaves the corpse, the corpse's equipment first,
    // then its inventory, then its Bag contents. An equippable item takes an empty compatible
    // equipment slot and never replaces an equipped item; equipped arrows are topped up. The
    // rest tops up carried stacks, Permanent Inventory first, and then takes empty slots in the
    // order GameService fills a purchase: matching specialized Bag slots, Permanent Inventory,
    // general Bag slots. A corpse Bag with contents takes an empty Bag slot whole; otherwise its
    // contents are looted and the emptied Bag follows them. A step takes part of a stack when the
    // stack does not fit, unless whole items are required, and nothing passes the carry-weight
    // hard cap. GameService and the client's prediction share this plan.
    public static class CorpseLootAllPlanner
    {
        public static LootAllPlan Plan(LootAllRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            return new Planner(request).Run();
        }

        private static int SectionOrder(string sectionKind)
        {
            switch (sectionKind)
            {
                case CorpseSectionKindIds.Equipment:
                    return 0;
                case CorpseSectionKindIds.GeneralInventory:
                    return 1;
                default:
                    return 2;
            }
        }

        private sealed class Stack
        {
            public Stack(Guid itemInstanceId, string definitionId, int quantity, string mergeKey)
            {
                ItemInstanceId = itemInstanceId;
                DefinitionId = definitionId;
                Quantity = quantity;
                MergeKey = mergeKey;
            }

            public Guid ItemInstanceId { get; }

            public string DefinitionId { get; }

            public int Quantity { get; set; }

            public string MergeKey { get; }

            public static Stack From(LootAllStack stack)
            {
                return stack == null
                    ? null
                    : new Stack(stack.ItemInstanceId, stack.DefinitionId, stack.Quantity, stack.MergeKey);
            }
        }

        private sealed class CarriedSlot
        {
            public CarriedSlot(LootAllCarriedSlot slot)
            {
                Source = slot;
                Occupant = Stack.From(slot.Occupant);
            }

            public LootAllCarriedSlot Source { get; }

            public Stack Occupant { get; set; }

            public bool IsSpecialized
            {
                get { return !string.Equals(Source.SlotKind, BagSlotKindIds.General, StringComparison.Ordinal); }
            }
        }

        private sealed class EquipmentSlot
        {
            public EquipmentSlot(LootAllEquipmentSlot slot, string[] blocks)
            {
                Entry = slot.Slot;
                Occupant = Stack.From(slot.Occupant);
                Blocks = blocks;
            }

            public ItemEquipmentSlotEntry Entry { get; }

            public Stack Occupant { get; set; }

            // The slots that the equipped item blocks.
            public string[] Blocks { get; set; }
        }

        private sealed class Planner
        {
            private readonly LootAllRequest request;
            private readonly List<EquipmentSlot> equipment;
            private readonly List<CarriedSlot> permanentInventory;
            private readonly List<LootAllStep> steps = new List<LootAllStep>();
            private readonly HashSet<Guid> equippedBagContents = new HashSet<Guid>();
            private List<CarriedSlot> bagContents;
            private long weight;
            private long capacity;

            public Planner(LootAllRequest request)
            {
                this.request = request;
                weight = request.CarriedWeight;
                capacity = request.CarryCapacity;
                equipment = request.Equipment
                    .Select(slot => new EquipmentSlot(slot, BlockedSlots(slot.Occupant)))
                    .ToList();
                permanentInventory = request.PermanentInventory.Select(slot => new CarriedSlot(slot)).ToList();
                bagContents = request.BagContents.Select(slot => new CarriedSlot(slot)).ToList();
            }

            public LootAllPlan Run()
            {
                var ordered = request.CorpseItems
                    .Select((item, index) => new { item, index })
                    .OrderBy(entry => SectionOrder(entry.item.SectionKind))
                    .ThenBy(entry => entry.index)
                    .Select(entry => entry.item)
                    .ToArray();
                var deferredBags = new List<LootAllCorpseItem>();
                var leftInCorpse = new HashSet<Guid>();
                foreach (var item in ordered)
                {
                    // The contents of a Bag that the looter equipped went along with it.
                    if (equippedBagContents.Contains(item.ItemInstanceId))
                    {
                        continue;
                    }

                    if (item.Bag != null && item.Bag.HasContents)
                    {
                        if (!TryEquipBag(item))
                        {
                            deferredBags.Add(item);
                        }

                        continue;
                    }

                    if (Take(item, item.Quantity) > 0)
                    {
                        leftInCorpse.Add(item.ItemInstanceId);
                    }
                }

                // A Bag whose contents all left the corpse is an empty Bag now.
                foreach (var bag in deferredBags)
                {
                    var contentsLeft = bag.Bag.Contents.Any(slot =>
                        slot.Occupant != null && leftInCorpse.Contains(slot.Occupant.ItemInstanceId));
                    if (!contentsLeft)
                    {
                        Take(bag, bag.Quantity, true);
                    }
                }

                return new LootAllPlan(steps.ToArray(), weight, capacity);
            }

            // Takes what fits of the item and returns the quantity left in the corpse.
            private int Take(LootAllCorpseItem item, int quantity, bool bagEmptied = false)
            {
                if (!item.CanTake || quantity <= 0)
                {
                    return quantity;
                }

                var definition = request.Definitions(item.DefinitionId);
                if (definition == null)
                {
                    return quantity;
                }

                if (item.Bag != null)
                {
                    if (TryEquipBag(item, bagEmptied))
                    {
                        return 0;
                    }

                    return TryPlaceInFreeSlot(item, definition, quantity, quantity) ? 0 : quantity;
                }

                var remaining = quantity;
                if (request.MeetsLevelRequirement(definition))
                {
                    remaining = TopUpEquipped(item, definition, remaining);
                    if (remaining > 0 && TryEquip(item, definition, remaining))
                    {
                        return 0;
                    }
                }

                remaining = MergeIntoCarried(item, definition, remaining);
                if (remaining > 0)
                {
                    remaining = PlaceInFreeSlot(item, definition, remaining);
                }

                return remaining;
            }

            private bool TryEquipBag(LootAllCorpseItem item, bool contentsLooted = false)
            {
                var definition = request.Definitions(item.DefinitionId);
                if (!item.CanTake
                    || definition == null
                    || !request.MeetsLevelRequirement(definition)
                    || !ItemEquipmentRules.IsCompatible(definition, ItemEquipmentSlotIds.Bag))
                {
                    return false;
                }

                var slot = equipment.FirstOrDefault(candidate => string.Equals(
                    candidate.Entry.Id,
                    ItemEquipmentSlotIds.Bag,
                    StringComparison.Ordinal));
                if (slot == null
                    || slot.Occupant != null
                    || !ItemEquipmentRules.CanHoldQuantity(definition, slot.Entry, item.Quantity)
                    || ItemEquipmentRules.FindBlockingSlot(definition, slot.Entry.Id, BlockedSlotsByOccupiedSlot()) != null)
                {
                    return false;
                }

                // The contents that are still in the Bag enter carried storage with it.
                var contents = item.Bag.Contents
                    .Select(content => new CarriedSlot(contentsLooted
                        ? new LootAllCarriedSlot(
                            content.ContainerId,
                            content.ContainerType,
                            content.SlotIndex,
                            content.SlotKind,
                            content.AcceptedTags,
                            null)
                        : content))
                    .ToList();
                long nextWeight;
                long nextCapacity;
                try
                {
                    nextWeight = weight;
                    foreach (var content in contents.Where(content => content.Occupant != null))
                    {
                        var contentDefinition = request.Definitions(content.Occupant.DefinitionId);
                        if (contentDefinition == null)
                        {
                            return false;
                        }

                        nextWeight = checked(nextWeight + ItemWeightRules.CalculateStackWeight(
                            contentDefinition.UnitWeight,
                            content.Occupant.Quantity));
                    }

                    nextCapacity = checked(capacity + item.Bag.CarryCapacityBonus);
                }
                catch (OverflowException)
                {
                    return false;
                }

                if (!IsAllowed(nextWeight, nextCapacity))
                {
                    return false;
                }

                weight = nextWeight;
                capacity = nextCapacity;
                slot.Occupant = new Stack(item.ItemInstanceId, item.DefinitionId, item.Quantity, null);
                slot.Blocks = definition.BlockedEquipmentSlots ?? Array.Empty<string>();
                bagContents = contents;
                foreach (var content in contents.Where(content => content.Occupant != null))
                {
                    equippedBagContents.Add(content.Occupant.ItemInstanceId);
                }

                steps.Add(new LootAllStep(
                    LootAllStepKind.EquipBag,
                    item,
                    item.Quantity,
                    true,
                    slot.Entry.Id,
                    Guid.Empty,
                    null));
                return true;
            }

            // Equipping is weightless, so only the slot rules decide.
            private int TopUpEquipped(LootAllCorpseItem item, ItemDefinition definition, int remaining)
            {
                if (item.MergeKey == null || definition.MaximumStackSize <= 1)
                {
                    return remaining;
                }

                foreach (var slot in equipment)
                {
                    var occupant = slot.Occupant;
                    if (remaining == 0)
                    {
                        break;
                    }

                    if (occupant == null
                        || !ItemEquipmentRules.IsCompatible(definition, slot.Entry.Id)
                        || !Merges(item, definition, occupant))
                    {
                        continue;
                    }

                    var room = ItemEquipmentRules.MaximumQuantity(definition, slot.Entry) - occupant.Quantity;
                    var quantity = (int)Math.Min(remaining, Math.Max(0, room));
                    if (quantity <= 0 || (request.WholeItemsOnly && quantity < remaining))
                    {
                        continue;
                    }

                    steps.Add(new LootAllStep(
                        LootAllStepKind.TopUpEquipped,
                        item,
                        quantity,
                        quantity == remaining,
                        slot.Entry.Id,
                        occupant.ItemInstanceId,
                        null));
                    occupant.Quantity += quantity;
                    remaining -= quantity;
                }

                return remaining;
            }

            private bool TryEquip(LootAllCorpseItem item, ItemDefinition definition, int quantity)
            {
                var blocked = BlockedSlotsByOccupiedSlot();
                foreach (var slot in equipment)
                {
                    if (slot.Occupant != null
                        || !ItemEquipmentRules.IsCompatible(definition, slot.Entry.Id)
                        || !ItemEquipmentRules.CanHoldQuantity(definition, slot.Entry, quantity)
                        || ItemEquipmentRules.FindBlockingSlot(definition, slot.Entry.Id, blocked) != null)
                    {
                        continue;
                    }

                    slot.Occupant = new Stack(item.ItemInstanceId, item.DefinitionId, quantity, item.MergeKey);
                    slot.Blocks = definition.BlockedEquipmentSlots ?? Array.Empty<string>();
                    steps.Add(new LootAllStep(
                        LootAllStepKind.Equip,
                        item,
                        quantity,
                        true,
                        slot.Entry.Id,
                        Guid.Empty,
                        null));
                    return true;
                }

                return false;
            }

            private int MergeIntoCarried(LootAllCorpseItem item, ItemDefinition definition, int remaining)
            {
                if (item.MergeKey == null || definition.MaximumStackSize <= 1)
                {
                    return remaining;
                }

                foreach (var slot in permanentInventory.Concat(bagContents))
                {
                    var occupant = slot.Occupant;
                    if (remaining == 0)
                    {
                        break;
                    }

                    if (occupant == null || !Merges(item, definition, occupant))
                    {
                        continue;
                    }

                    var quantity = Math.Min(remaining, definition.MaximumStackSize - occupant.Quantity);
                    if (quantity <= 0 || (request.WholeItemsOnly && quantity < remaining))
                    {
                        continue;
                    }

                    var fitting = FittingQuantity(definition, quantity);
                    if (fitting <= 0 || (request.WholeItemsOnly && fitting < remaining))
                    {
                        // Carried weight only grows from here, so nothing more of the item fits.
                        return remaining;
                    }

                    steps.Add(new LootAllStep(
                        LootAllStepKind.Merge,
                        item,
                        fitting,
                        fitting == remaining,
                        string.Empty,
                        occupant.ItemInstanceId,
                        null));
                    AddWeight(definition, fitting);
                    occupant.Quantity += fitting;
                    remaining -= fitting;
                    if (fitting < quantity)
                    {
                        return remaining;
                    }
                }

                return remaining;
            }

            private int PlaceInFreeSlot(LootAllCorpseItem item, ItemDefinition definition, int remaining)
            {
                return TryPlaceInFreeSlot(item, definition, remaining, remaining) ? 0 : remaining;
            }

            // Places the rest of the item, or as much of it as the hard cap allows, into the first
            // free slot that accepts it, and returns whether the whole rest left the corpse.
            private bool TryPlaceInFreeSlot(
                LootAllCorpseItem item,
                ItemDefinition definition,
                int remaining,
                int requiredQuantity)
            {
                var slot = FindFreeSlot(item, definition);
                if (slot == null)
                {
                    return false;
                }

                var quantity = FittingQuantity(definition, remaining);
                var minimum = request.WholeItemsOnly || item.Bag != null ? requiredQuantity : 1;
                if (quantity < minimum || quantity <= 0)
                {
                    return false;
                }

                var takesRest = quantity == remaining;
                steps.Add(new LootAllStep(
                    LootAllStepKind.Place,
                    item,
                    quantity,
                    takesRest,
                    string.Empty,
                    Guid.Empty,
                    slot.Source));
                AddWeight(definition, quantity);
                // A split stack gets an id only when GameService commits it, so nothing merges into
                // it within the plan.
                slot.Occupant = takesRest
                    ? new Stack(item.ItemInstanceId, item.DefinitionId, quantity, item.MergeKey)
                    : new Stack(Guid.Empty, item.DefinitionId, quantity, null);
                return takesRest;
            }

            private CarriedSlot FindFreeSlot(LootAllCorpseItem item, ItemDefinition definition)
            {
                return bagContents.Where(slot => slot.IsSpecialized)
                    .Concat(permanentInventory)
                    .Concat(bagContents.Where(slot => !slot.IsSpecialized))
                    .FirstOrDefault(slot => slot.Occupant == null && Accepts(item, definition, slot.Source));
            }

            private static bool Accepts(LootAllCorpseItem item, ItemDefinition definition, LootAllCarriedSlot slot)
            {
                if (item.Bag != null)
                {
                    return BagDestinationRules.TryResolve(slot.ContainerType, slot.SlotKind, out var destination)
                        && BagLocationRules.CanPlace(false, destination, false);
                }

                return ItemSlotRules.Accepts(
                    definition,
                    new BagSlotDefinition
                    {
                        Index = slot.SlotIndex,
                        Kind = slot.SlotKind,
                        AcceptedTags = slot.AcceptedTags.ToArray()
                    });
            }

            private bool Merges(LootAllCorpseItem item, ItemDefinition definition, Stack stack)
            {
                return stack.ItemInstanceId != Guid.Empty
                    && stack.MergeKey != null
                    && string.Equals(stack.MergeKey, item.MergeKey, StringComparison.Ordinal)
                    && string.Equals(stack.DefinitionId, definition.Id, StringComparison.Ordinal);
            }

            // The largest part of the quantity that keeps the carry state within the hard cap.
            private int FittingQuantity(ItemDefinition definition, int quantity)
            {
                if (quantity <= 0)
                {
                    return 0;
                }

                if (FitsWeight(definition, quantity))
                {
                    return quantity;
                }

                var low = 0;
                var high = quantity - 1;
                while (low < high)
                {
                    var middle = low + ((high - low + 1) / 2);
                    if (FitsWeight(definition, middle))
                    {
                        low = middle;
                    }
                    else
                    {
                        high = middle - 1;
                    }
                }

                return low;
            }

            private bool FitsWeight(ItemDefinition definition, int quantity)
            {
                try
                {
                    return IsAllowed(
                        checked(weight + ItemWeightRules.CalculateStackWeight(definition.UnitWeight, quantity)),
                        capacity);
                }
                catch (OverflowException)
                {
                    return false;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false;
                }
            }

            private void AddWeight(ItemDefinition definition, int quantity)
            {
                weight = checked(weight + ItemWeightRules.CalculateStackWeight(definition.UnitWeight, quantity));
            }

            // GameService checks the whole transaction against the carry state it started with.
            private bool IsAllowed(long nextWeight, long nextCapacity)
            {
                return nextWeight >= 0
                    && nextCapacity > 0
                    && EncumbranceRules.IsAllowedVoluntaryTransition(
                        request.CarriedWeight,
                        request.CarryCapacity,
                        nextWeight,
                        nextCapacity);
            }

            private Dictionary<string, string[]> BlockedSlotsByOccupiedSlot()
            {
                var blocked = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var slot in equipment)
                {
                    if (slot.Occupant != null)
                    {
                        blocked[slot.Entry.Id] = slot.Blocks ?? Array.Empty<string>();
                    }
                }

                return blocked;
            }

            private string[] BlockedSlots(LootAllStack occupant)
            {
                if (occupant == null)
                {
                    return Array.Empty<string>();
                }

                return request.Definitions(occupant.DefinitionId)?.BlockedEquipmentSlots ?? Array.Empty<string>();
            }
        }
    }
}
