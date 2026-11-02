using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Ui
{
    // Where the equipment window shows each equipment slot: a column left of the character, a
    // column right of it, and rows below it. Slots the layout does not know, such as ones added
    // to the catalog later, fill extra rows below in their given order.
    public sealed class PaperDollArrangement
    {
        public PaperDollArrangement(
            IReadOnlyList<string> left,
            IReadOnlyList<string> right,
            IReadOnlyList<IReadOnlyList<string>> bottomRows)
        {
            Left = left;
            Right = right;
            BottomRows = bottomRows;
        }

        public IReadOnlyList<string> Left { get; }

        public IReadOnlyList<string> Right { get; }

        public IReadOnlyList<IReadOnlyList<string>> BottomRows { get; }

        public IEnumerable<string> All
        {
            get { return Left.Concat(Right).Concat(BottomRows.SelectMany(row => row)); }
        }
    }

    public static class EquipmentPaperDollLayout
    {
        public const int BottomColumns = 4;

        // Cloaks are achievement rewards that will be worn from their own window, so no item
        // equips to the Back slot and the paper doll leaves it out (Victor, 2026-11-02).
        private static readonly HashSet<string> HiddenSlots =
            new HashSet<string>(StringComparer.Ordinal) { "back" };

        private static readonly string[] LeftSlots = { "head", "shoulders", "body", "legs", "feet" };
        private static readonly string[] RightSlots = { "neck", "hands", "ring_1", "ring_2", "bag" };
        private static readonly string[] BottomSlots = { "main_hand", "off_hand", "ammunition", "tool" };

        public static PaperDollArrangement Arrange(IEnumerable<string> slotIds)
        {
            var present = (slotIds ?? Enumerable.Empty<string>())
                .Where(slotId => !string.IsNullOrWhiteSpace(slotId) && !HiddenSlots.Contains(slotId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var shown = new HashSet<string>(present, StringComparer.Ordinal);
            var known = new HashSet<string>(
                LeftSlots.Concat(RightSlots).Concat(BottomSlots),
                StringComparer.Ordinal);
            var rows = new List<IReadOnlyList<string>>();
            var bottom = BottomSlots.Where(shown.Contains).ToArray();
            if (bottom.Length > 0)
            {
                rows.Add(bottom);
            }

            var unknown = present.Where(slotId => !known.Contains(slotId)).ToArray();
            for (var start = 0; start < unknown.Length; start += BottomColumns)
            {
                rows.Add(unknown.Skip(start).Take(BottomColumns).ToArray());
            }

            return new PaperDollArrangement(
                LeftSlots.Where(shown.Contains).ToArray(),
                RightSlots.Where(shown.Contains).ToArray(),
                rows);
        }
    }

    // The color of the frame around an item, which shows its rarity. Items have no rarity yet,
    // so every item is Common; a rarity in the catalog will pick its color from the theme.
    public static class ItemRarityPresentation
    {
        public static UnityEngine.Color FrameColor(UiTheme theme, ClientItemCatalog catalog, string definitionId)
        {
            return theme != null ? theme.RarityCommon : UnityEngine.Color.white;
        }
    }

    // The inventory search: an item matches when its name or category contains the query,
    // ignoring case. An empty query matches every item.
    public static class InventorySearch
    {
        public static bool Matches(ClientItemCatalog catalog, string definitionId, string query)
        {
            var trimmed = query?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return true;
            }

            if (catalog == null || string.IsNullOrEmpty(definitionId))
            {
                return false;
            }

            return Contains(catalog.GetDisplayName(definitionId), trimmed)
                || Contains(InventorySlotPresentation.CategoryName(catalog, definitionId), trimmed);
        }

        private static bool Contains(string value, string query)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public enum CarryLoadLevel
    {
        Normal,
        Slowed,
        AtHardCap
    }

    // The carry meter spans zero to the hard cap, with a marker at full carry capacity.
    public readonly struct CarryLoadPresentation
    {
        private const int FullCapacityBasisPoints = 10_000;
        private const int HardCapBasisPoints =
            EncumbranceRules.HardCapLoadPercent * 100;

        private CarryLoadPresentation(
            long carriedWeight,
            long carryCapacity,
            int loadRatioBasisPoints,
            int movementMultiplierBasisPoints)
        {
            CarriedWeight = carriedWeight;
            CarryCapacity = carryCapacity;
            LoadRatioBasisPoints = Math.Max(0, loadRatioBasisPoints);
            MovementMultiplierBasisPoints = Math.Max(0, movementMultiplierBasisPoints);
        }

        public long CarriedWeight { get; }

        public long CarryCapacity { get; }

        public int LoadRatioBasisPoints { get; }

        public int MovementMultiplierBasisPoints { get; }

        public float FillFraction
        {
            get
            {
                if (Level == CarryLoadLevel.AtHardCap)
                {
                    return 1f;
                }

                if (!HasExactWeights)
                {
                    return Math.Min(1f, LoadRatioBasisPoints / (float)HardCapBasisPoints);
                }

                return (float)Math.Min(
                    1d,
                    CarriedWeight * 100d / (CarryCapacity * (double)EncumbranceRules.HardCapLoadPercent));
            }
        }

        public float FullCapacityMarkerFraction
        {
            get { return FullCapacityBasisPoints / (float)HardCapBasisPoints; }
        }

        // The snapshot load ratio is floored, so a character at the exact hard cap can
        // report slightly less than 140 percent. The level uses the exact weights and the
        // shared hard-cap rule instead.
        public CarryLoadLevel Level
        {
            get
            {
                if (!HasExactWeights)
                {
                    if (LoadRatioBasisPoints >= HardCapBasisPoints)
                    {
                        return CarryLoadLevel.AtHardCap;
                    }

                    return LoadRatioBasisPoints > FullCapacityBasisPoints
                        ? CarryLoadLevel.Slowed
                        : CarryLoadLevel.Normal;
                }

                if (CarriedWeight <= CarryCapacity)
                {
                    return CarryLoadLevel.Normal;
                }

                // At the hard cap no further weight unit is allowed.
                return CarriedWeight == long.MaxValue
                    || !EncumbranceRules.IsWithinHardCap(CarriedWeight + 1, CarryCapacity)
                        ? CarryLoadLevel.AtHardCap
                        : CarryLoadLevel.Slowed;
            }
        }

        private bool HasExactWeights
        {
            get { return CarryCapacity > 0 && CarriedWeight >= 0; }
        }

        public string LoadPercentText
        {
            get { return FormatBasisPoints(LoadRatioBasisPoints) + "%"; }
        }

        public string SpeedPercentText
        {
            get { return FormatBasisPoints(MovementMultiplierBasisPoints) + "%"; }
        }

        public string WeightText
        {
            get
            {
                return CarriedWeight.ToString(CultureInfo.InvariantCulture)
                    + " / "
                    + CarryCapacity.ToString(CultureInfo.InvariantCulture);
            }
        }

        public static CarryLoadPresentation From(CharacterInventorySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            return From(
                snapshot.CarriedWeight,
                snapshot.CarryCapacity,
                snapshot.LoadRatioBasisPoints,
                snapshot.MovementMultiplierBasisPoints);
        }

        public static CarryLoadPresentation From(
            long carriedWeight,
            long carryCapacity,
            int loadRatioBasisPoints,
            int movementMultiplierBasisPoints)
        {
            return new CarryLoadPresentation(
                carriedWeight,
                carryCapacity,
                loadRatioBasisPoints,
                movementMultiplierBasisPoints);
        }

        private static string FormatBasisPoints(int basisPoints)
        {
            return (basisPoints / 100f).ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    // Labels and tooltips for item slots. Everything here reads committed state only.
    public static class InventorySlotPresentation
    {
        public static string QuantityText(int quantity)
        {
            return quantity > 1
                ? quantity.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        // A live count that differs from the committed quantity always shows, even 0 or 1.
        public static string LiveQuantityText(int committedQuantity, int liveQuantity)
        {
            return liveQuantity != committedQuantity
                ? Math.Max(0, liveQuantity).ToString(CultureInfo.InvariantCulture)
                : QuantityText(committedQuantity);
        }

        public static string LiveQuantityDescription(
            string description,
            int committedQuantity,
            int liveQuantity)
        {
            return liveQuantity != committedQuantity
                ? description + "\n" + Math.Max(0, liveQuantity).ToString(CultureInfo.InvariantCulture)
                    + " left; spent arrows commit in batches"
                : description;
        }

        // P marks protection on death and I marks insurance.
        public static string PolicyBadge(InventoryItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            var badge = new StringBuilder();
            if (HasActivePolicy(item, ItemPolicyIds.ProtectedOnDeath))
            {
                badge.Append('P');
            }

            if (HasActivePolicy(item, ItemPolicyIds.Insured))
            {
                badge.Append('I');
            }

            return badge.ToString();
        }

        public static string AcceptedTagsLabel(
            ClientItemCatalog catalog,
            IReadOnlyList<string> acceptedTags)
        {
            if (acceptedTags == null || acceptedTags.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                "/",
                acceptedTags.Select(tag => IdentityName(catalog?.Gameplay?.Tags, tag)));
        }

        public static string EquipmentSlotName(ClientItemCatalog catalog, string slotId)
        {
            return catalog != null
                && catalog.TryGetEquipmentSlot(slotId, out var slot)
                && !string.IsNullOrWhiteSpace(slot.DisplayName)
                    ? slot.DisplayName
                    : slotId ?? string.Empty;
        }

        // The first equipped item whose presentation has lowered garments, such as a hood that
        // can be pushed back, or null; the inventory offers to lower or raise it.
        public static InventoryItem LowerableHeadwear(IReadOnlyList<InventoryEquipmentSlot> equipment, ClientItemCatalog catalog)
        {
            if (equipment == null || catalog == null || catalog.Presentation == null)
            {
                return null;
            }

            foreach (var slot in equipment.OrderBy(slot => slot.SortOrder))
            {
                if (slot.Item != null
                    && catalog.Presentation.TryGet(slot.Item.DefinitionId, out var entry)
                    && entry.loweredGarments != null
                    && entry.loweredGarments.Length > 0)
                {
                    return slot.Item;
                }
            }

            return null;
        }

        public static string CategoryId(ClientItemCatalog catalog, string definitionId)
        {
            return catalog != null && catalog.TryGetDefinition(definitionId, out var definition)
                ? definition.Category ?? string.Empty
                : string.Empty;
        }

        // The display name of the item's category, such as Material, or empty.
        public static string CategoryName(ClientItemCatalog catalog, string definitionId)
        {
            var categoryId = CategoryId(catalog, definitionId);
            return categoryId.Length > 0
                ? IdentityName(catalog.Gameplay?.Categories, categoryId)
                : string.Empty;
        }

        public static string ContainerName(InventoryClientState state, string containerType)
        {
            return containerType switch
            {
                ItemContainerTypeIds.PermanentInventory => "Permanent Inventory",
                ItemContainerTypeIds.BagContents => state?.FullSnapshot?.EquippedBag != null
                    ? state.Catalog.GetDisplayName(state.FullSnapshot.EquippedBag.Item.DefinitionId)
                    : "Bag",
                ItemContainerTypeIds.Bank => "Bank",
                _ => containerType ?? string.Empty
            };
        }

        public static string BlockedSlotText(ClientItemCatalog catalog, InventoryEquipmentSlot blocker)
        {
            return "Blocked by " + catalog.GetDisplayName(blocker.Item.DefinitionId)
                + " in " + EquipmentSlotName(catalog, blocker.SlotId)
                + ". Unequip it to use this slot.";
        }

        public static string EmptyEquipmentSlotText(ClientItemCatalog catalog, string slotId)
        {
            return catalog != null
                && catalog.TryGetEquipmentSlot(slotId, out var definition)
                && definition.MaximumQuantity > 1
                    ? "Empty. Holds a stack of up to "
                        + definition.MaximumQuantity.ToString(CultureInfo.InvariantCulture) + "."
                    : "Empty";
        }

        public static string CorpseSectionTitle(string sectionKind)
        {
            return sectionKind switch
            {
                CorpseSectionKindIds.Equipment => "Corpse Equipment",
                CorpseSectionKindIds.GeneralInventory => "Corpse Inventory",
                CorpseSectionKindIds.Bag => "Corpse Bag Contents",
                _ => "Corpse " + sectionKind
            };
        }

        // The equipment slot name or the accepted item types of a corpse slot; empty for a
        // general slot.
        public static string CorpseSlotLabel(ClientItemCatalog catalog, CorpseLootSlot slot)
        {
            if (!string.IsNullOrWhiteSpace(slot.EquipmentSlotId))
            {
                return EquipmentSlotName(catalog, slot.EquipmentSlotId);
            }

            return string.Equals(slot.SlotKind, BagSlotKindIds.General, StringComparison.Ordinal)
                ? string.Empty
                : AcceptedTagsLabel(catalog, slot.AcceptedTags);
        }

        public static string RecoverySourceLabel(string source)
        {
            return source switch
            {
                "death_recovered_insured" => "Recovered: consumed insurance",
                "death_recovered_protected" => "Recovered: protected on death",
                "secure_container_removal" => "Recovered: from the removed Warded Pouch",
                "cloak_equipment_removal" => "Recovered: cloaks can no longer be worn",
                _ => "Recovered: " + source
            };
        }

        // Empty equipment slots that an item equipped elsewhere blocks, keyed by the blocked
        // slot id, with the blocking equipment slot as value.
        public static IReadOnlyDictionary<string, InventoryEquipmentSlot> FindBlockedEquipmentSlots(
            ClientItemCatalog catalog,
            IReadOnlyList<InventoryEquipmentSlot> equipment)
        {
            var blocked = new Dictionary<string, InventoryEquipmentSlot>(StringComparer.Ordinal);
            if (catalog == null || equipment == null)
            {
                return blocked;
            }

            var emptySlots = new HashSet<string>(
                equipment.Where(slot => slot != null && slot.Item == null).Select(slot => slot.SlotId),
                StringComparer.Ordinal);
            foreach (var occupied in equipment)
            {
                if (occupied?.Item == null
                    || !catalog.TryGetDefinition(occupied.Item.DefinitionId, out var definition)
                    || definition.BlockedEquipmentSlots == null)
                {
                    continue;
                }

                foreach (var blockedSlotId in definition.BlockedEquipmentSlots)
                {
                    if (emptySlots.Contains(blockedSlotId) && !blocked.ContainsKey(blockedSlotId))
                    {
                        blocked.Add(blockedSlotId, occupied);
                    }
                }
            }

            return blocked;
        }

        public static string BuildItemTooltip(
            ClientItemCatalog catalog,
            InventoryItem item,
            string slotDescription)
        {
            if (catalog == null || item == null)
            {
                return string.Empty;
            }

            var text = new StringBuilder();
            text.Append("<b>").Append(catalog.GetDisplayName(item.DefinitionId)).Append("</b>");
            if (!catalog.TryGetDefinition(item.DefinitionId, out var definition))
            {
                AppendLine(text, "Quantity " + item.Quantity.ToString(CultureInfo.InvariantCulture));
                AppendSlot(text, slotDescription);
                return text.ToString();
            }

            var category = IdentityName(catalog.Gameplay?.Categories, definition.Category);
            AppendLine(
                text,
                category
                    + "  ·  Quantity "
                    + item.Quantity.ToString(CultureInfo.InvariantCulture)
                    + " / "
                    + definition.MaximumStackSize.ToString(CultureInfo.InvariantCulture));
            var weight = ItemWeightRules.CalculateStackWeight(
                definition.UnitWeight,
                Math.Max(1, item.Quantity));
            AppendLine(
                text,
                "Weight "
                    + weight.ToString(CultureInfo.InvariantCulture)
                    + (item.Quantity > 1
                        ? " (" + definition.UnitWeight.ToString(CultureInfo.InvariantCulture) + " each)"
                        : string.Empty));
            if (definition.EquipmentSlots != null && definition.EquipmentSlots.Length > 0)
            {
                var equips = "Equips to "
                    + string.Join(
                        " or ",
                        definition.EquipmentSlots.Select(slotId => EquipmentSlotName(catalog, slotId)));
                if (definition.BlockedEquipmentSlots != null
                    && definition.BlockedEquipmentSlots.Length > 0)
                {
                    equips += ", blocks "
                        + string.Join(
                            " and ",
                            definition.BlockedEquipmentSlots.Select(slotId =>
                                EquipmentSlotName(catalog, slotId)));
                }

                AppendLine(text, equips);
            }

            foreach (var policy in item.Policies.Where(IsActive))
            {
                AppendLine(text, PolicyName(policy.Kind) + " (" + ProtectionSourceName(policy.ProtectionSource) + ")");
            }

            if (!definition.PlayerDestroyable)
            {
                AppendLine(text, "Cannot be destroyed");
            }

            AppendSlot(text, slotDescription);
            return text.ToString();
        }

        public static string ProtectionSourceName(string source)
        {
            return source switch
            {
                "insurance_npc" => "Insurance NPC",
                "quest_grant" => "Quest grant",
                "catalog_default" => "Catalog rule",
                _ => "System"
            };
        }

        public static string PolicyName(string policyKind)
        {
            return policyKind switch
            {
                ItemPolicyIds.ProtectedOnDeath => "Protected on death",
                ItemPolicyIds.Insured => "Insured",
                _ => policyKind ?? string.Empty
            };
        }

        private static void AppendSlot(StringBuilder text, string slotDescription)
        {
            if (!string.IsNullOrWhiteSpace(slotDescription))
            {
                AppendLine(text, slotDescription);
            }
        }

        private static void AppendLine(StringBuilder text, string line)
        {
            text.Append('\n').Append(line);
        }

        private static bool HasActivePolicy(InventoryItem item, string policyKind)
        {
            return item.Policies.Any(policy =>
                IsActive(policy)
                && string.Equals(policy.Kind, policyKind, StringComparison.Ordinal));
        }

        private static bool IsActive(InventoryPolicy policy)
        {
            return policy != null
                && string.Equals(policy.Status, ItemPolicyRules.ActiveStatus, StringComparison.Ordinal);
        }

        private static string IdentityName(IEnumerable<ItemCatalogIdentityEntry> entries, string id)
        {
            var entry = entries?.FirstOrDefault(candidate =>
                candidate != null && string.Equals(candidate.Id, id, StringComparison.Ordinal));
            return entry != null && !string.IsNullOrWhiteSpace(entry.DisplayName)
                ? entry.DisplayName
                : id ?? string.Empty;
        }
    }
}
