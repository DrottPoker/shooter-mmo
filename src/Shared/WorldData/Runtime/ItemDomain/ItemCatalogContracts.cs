using System;

namespace Mmorpg.WorldData.Items
{
    public static class ItemCatalogFormat
    {
        public const int Version = 8;

        // Hashed into every structural fingerprint instead of the format version, so a new
        // format that adds non-structural fields leaves persisted definitions compatible. It
        // changes only when the meaning of a structural field changes.
        public const int StructuralFingerprintVersion = 4;

        // Tags and equipment slot ids travel in realtime item and corpse messages, so
        // they must fit the protocol limits that RealtimeProtocolLimits defines.
        public const int MaximumIdentifierLength = 128;
        public const int MaximumTagLength = 64;
        public const int MaximumEquipmentSlotIdLength = 64;
        public const int MaximumAcceptedTagsPerSlot = 16;

        // An equipment slot quantity and a stack size both fit the protocol's i32 quantity.
        public const int MaximumQuantity = 1_000_000;
        public const int MaximumContainerSlotCapacity = 65535;

        // Base item value in currency units. A vendor price never exceeds the maximum balance.
        public const long MaximumItemValue = 1_000_000_000L;
        public const int MaximumCurrencyDisplayNameLength = 32;
        public const int MaximumOffersPerVendor = 32;
        public const int MaximumToolKindsPerDefinition = 8;
    }

    // Only the ids that rules give a meaning to. Every other category, tag, and
    // equipment slot is content.
    public static class ItemCategoryIds
    {
        // Weapons carry weapon combat data.
        public const string Weapon = "weapon";

        // Ammunition carries ammunition combat data.
        public const string Ammunition = "ammunition";

        // Bag definitions own a Bag layout.
        public const string Bag = "bag";

        // A protected quest item cannot be destroyed by the player.
        public const string QuestItem = "quest_item";
    }

    public static class ItemEquipmentSlotIds
    {
        // The equipped Bag activates its contents and adds its carry-capacity bonus.
        public const string Bag = "bag";

        // Gathering requires a tool of the right kind equipped in this slot.
        public const string Tool = "tool";

        // The weapon in this slot attacks.
        public const string MainHand = "main_hand";

        // A Ranged weapon fires the stack in this slot.
        public const string Ammunition = "ammunition";
    }

    public static class ItemPolicyIds
    {
        public const string ProtectedOnDeath = "protected_on_death";
        public const string Insured = "insured";
    }

    public static class BagSlotKindIds
    {
        public const string General = "general";
        public const string Specialized = "specialized";
    }

    [Serializable]
    public sealed class ItemCatalogAuthoringDocument
    {
        public int FormatVersion;
        public string CatalogId;
        public ItemCharacterSettingsEntry Character;
        public ItemEconomySettingsEntry Economy;
        public ItemProgressionSettingsEntry Progression;
        public ItemCatalogIdentityEntry[] Categories;
        public ItemCatalogIdentityEntry[] Tags;
        public ItemCatalogIdentityEntry[] ToolKinds;
        public ItemEquipmentSlotEntry[] EquipmentSlots;
        public ItemWeaponFamilyEntry[] WeaponFamilies;
        public ItemCatalogIdentityEntry[] AmmunitionFamilies;
        public ItemDefinitionAuthoringEntry[] Definitions;
        public VendorProfileAuthoringEntry[] Vendors;
        public ItemCatalogRetiredIdsEntry Retired;
    }

    // Per-character item settings that every new character starts with.
    [Serializable]
    public sealed class ItemCharacterSettingsEntry
    {
        public long BaseCarryCapacity;
        public int InventorySlotCount;
        public int BankSlotCount;
    }

    // The character currency: its display name and the balance every new character starts
    // with. The balance itself is character state that GameService owns.
    [Serializable]
    public sealed class ItemEconomySettingsEntry
    {
        public string CurrencyDisplayName;
        public long StartingBalance = -1;
    }

    // Character progression limits. Every level requirement must fit them.
    [Serializable]
    public sealed class ItemProgressionSettingsEntry
    {
        public int MaximumCharacterLevel;
    }

    [Serializable]
    public sealed class ItemCatalogIdentityEntry
    {
        public string Id;
        public string DisplayName;
    }

    // Equipment slots are listed in display order. A slot holds one item instance whose
    // quantity is at most MaximumQuantity, so a stack only fits a slot that allows it.
    [Serializable]
    public sealed class ItemEquipmentSlotEntry
    {
        public string Id;
        public string DisplayName;
        public int MaximumQuantity;
    }

    // A weapon family names its combat style and, for Ranged, the ammunition family it
    // fires. Projectiles fly at the projectile speed; 0 hits at once and suits Melee. An
    // attack releases its blow, arrow, or bolt the windup after it starts, when the body's
    // attack clip strikes or looses; 0 releases at once.
    [Serializable]
    public sealed class ItemWeaponFamilyEntry
    {
        public string Id;
        public string DisplayName;
        public string CombatStyle;
        public string AmmunitionFamily;
        public float ProjectileSpeed = -1f;
        public float WindupSeconds = -1f;
    }

    // Weapon combat data: base damage roll, damage type, attack interval, range, and the mana
    // every attack of a Magic weapon costs.
    [Serializable]
    public sealed class ItemWeaponEntry
    {
        public string WeaponFamily;
        public string DamageType;
        public int MinimumDamage = -1;
        public int MaximumDamage = -1;
        public float AttackIntervalSeconds;
        public float Range;
        public int ManaCost = -1;
    }

    // Ammunition combat data: its family and its bonus in percent of the weapon roll.
    [Serializable]
    public sealed class ItemAmmunitionEntry
    {
        public string AmmunitionFamily;
        public int DamageBonusPercent = -1;
    }

    // Ids that were released and removed. A retired id can never return with another
    // meaning, and GameService retires its rows once nothing references them.
    [Serializable]
    public sealed class ItemCatalogRetiredIdsEntry
    {
        public string[] Categories;
        public string[] Tags;
        public string[] EquipmentSlots;
        public string[] Definitions;
    }

    [Serializable]
    public sealed class ItemDefinitionAuthoringEntry
    {
        public string Id;
        public string DisplayName;
        public string Category;
        public string[] Tags;
        public string[] ToolKinds;
        public long UnitWeight = -1;
        public long Value = -1;
        public int MaximumStackSize;
        public string[] EquipmentSlots;
        public string[] BlockedEquipmentSlots;
#pragma warning disable UAC1001 // Never Unity-serialized; null marks a missing authoring field.
        public bool? PlayerDestroyable;
#pragma warning restore UAC1001
        public string[] DefaultPolicies;
        public BagDefinitionAuthoringEntry Bag;
        public int RequiredCharacterLevel;
        public ItemWeaponEntry Weapon;
        public ItemAmmunitionEntry Ammunition;
    }

    [Serializable]
    public sealed class BagDefinitionAuthoringEntry
    {
        public long CarryCapacityBonus = -1;
        public BagSlotAuthoringEntry[] Slots;
    }

    [Serializable]
    public sealed class BagSlotAuthoringEntry
    {
        public int Index = -1;
        public string Kind;
        public string[] AcceptedTags;
    }

    // An NPC vendor's offers and prices. Rates are named from the player's side: the player
    // purchases offers at PurchasePriceBasisPoints of item value and sells items of the sale
    // categories at SalePriceBasisPoints.
    [Serializable]
    public sealed class VendorProfileAuthoringEntry
    {
        public string Id;
        public string DisplayName;
        public VendorOfferEntry[] Offers;
        public int PurchasePriceBasisPoints;
        public int SalePriceBasisPoints = -1;
        public string[] SaleCategories;
    }

    [Serializable]
    public sealed class VendorOfferEntry
    {
        public string DefinitionId;
    }

    [Serializable]
    public sealed class ItemCatalogRuntimeDocument
    {
        public int FormatVersion;
        public string CatalogId;
        public string Revision;
        public ItemCharacterSettingsEntry Character;
        public ItemEconomySettingsEntry Economy;
        public ItemProgressionSettingsEntry Progression;
        public ItemCatalogIdentityEntry[] Categories;
        public ItemCatalogIdentityEntry[] Tags;
        public ItemCatalogIdentityEntry[] ToolKinds;
        public ItemEquipmentSlotEntry[] EquipmentSlots;
        public ItemWeaponFamilyEntry[] WeaponFamilies;
        public ItemCatalogIdentityEntry[] AmmunitionFamilies;
        public ItemDefinition[] Definitions;
        public VendorProfileDefinition[] Vendors;
        public ItemCatalogRetiredIdsEntry Retired;
    }

    [Serializable]
    public sealed class ItemDefinition
    {
        public string Id;
        public string DisplayName;
        public string Category;
        public string[] Tags;
        public string[] ToolKinds;
        public long UnitWeight;
        public long Value;
        public int MaximumStackSize;
        public string[] EquipmentSlots;
        public string[] BlockedEquipmentSlots;
        public bool PlayerDestroyable;
        public string[] DefaultPolicies;
        public BagDefinition Bag;
        public int RequiredCharacterLevel;
        public ItemWeaponEntry Weapon;
        public ItemAmmunitionEntry Ammunition;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class BagDefinition
    {
        public long CarryCapacityBonus;
        public BagSlotDefinition[] Slots;
    }

    [Serializable]
    public sealed class BagSlotDefinition
    {
        public int Index;
        public string Kind;
        public string[] AcceptedTags;
    }

    [Serializable]
    public sealed class VendorProfileDefinition
    {
        public string Id;
        public string DisplayName;
        public VendorOfferEntry[] Offers;
        public int PurchasePriceBasisPoints;
        public int SalePriceBasisPoints;
        public string[] SaleCategories;
    }
}
