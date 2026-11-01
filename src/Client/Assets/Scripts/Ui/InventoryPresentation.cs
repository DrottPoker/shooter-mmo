using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Ui
{
    public readonly struct PaperDollCell
    {
        public PaperDollCell(string slotId, int row, int column)
        {
            SlotId = slotId;
            Row = row;
            Column = column;
        }

        public string SlotId { get; }

        public int Row { get; }

        public int Column { get; }
    }

    // Equipment slots laid out like a character silhouette. Slots the layout does not know,
    // such as ones added to the catalog later, fill extra rows in their given order.
    public static class EquipmentPaperDollLayout
    {
        public const int ColumnCount = 3;

        private static readonly Dictionary<string, (int Row, int Column)> KnownCells =
            new Dictionary<string, (int Row, int Column)>(StringComparer.Ordinal)
            {
                ["tool"] = (0, 0),
                ["head"] = (0, 1),
                ["bag"] = (0, 2),
                ["shoulders"] = (1, 0),
                ["neck"] = (1, 1),
                ["back"] = (1, 2),
                ["main_hand"] = (2, 0),
                ["body"] = (2, 1),
                ["off_hand"] = (2, 2),
                ["hands"] = (3, 0),
                ["legs"] = (3, 1),
                ["ammunition"] = (3, 2),
                ["ring_1"] = (4, 0),
                ["feet"] = (4, 1),
                ["ring_2"] = (4, 2)
            };

        public static IReadOnlyList<PaperDollCell> Arrange(
            IEnumerable<string> slotIds,
            out int rowCount)
        {
            var distinct = (slotIds ?? Enumerable.Empty<string>())
                .Where(slotId => !string.IsNullOrWhiteSpace(slotId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var cells = new List<PaperDollCell>(distinct.Length);
            var unknown = new List<string>();
            var dollRows = 0;
            foreach (var slotId in distinct)
            {
                if (KnownCells.TryGetValue(slotId, out var cell))
                {
                    cells.Add(new PaperDollCell(slotId, cell.Row, cell.Column));
                    dollRows = Math.Max(dollRows, cell.Row + 1);
                }
                else
                {
                    unknown.Add(slotId);
                }
            }

            for (var index = 0; index < unknown.Count; index++)
            {
                cells.Add(new PaperDollCell(
                    unknown[index],
                    dollRows + index / ColumnCount,
                    index % ColumnCount));
            }

            rowCount = dollRows + (unknown.Count + ColumnCount - 1) / ColumnCount;
            return cells
                .OrderBy(cell => cell.Row)
                .ThenBy(cell => cell.Column)
                .ToArray();
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
