using System;

namespace Mmorpg.WorldData.Items
{
    public static class ItemCatalogFormat
    {
        public const int Version = 3;

        // Tags and equipment slot ids travel in realtime item and corpse messages, so
        // they must fit the protocol limits that RealtimeProtocolLimits defines.
        public const int MaximumIdentifierLength = 128;
        public const int MaximumTagLength = 64;
        public const int MaximumEquipmentSlotIdLength = 64;
        public const int MaximumAcceptedTagsPerSlot = 16;

        // An equipment slot quantity and a stack size both fit the protocol's i32 quantity.
        public const int MaximumQuantity = 1_000_000;
        public const int MaximumContainerSlotCapacity = 65535;
    }

    // Only the ids that rules give a meaning to. Every other category, tag, and
    // equipment slot is content.
    public static class ItemCategoryIds
    {
        // Weapons are never Secure Container eligible.
        public const string Weapon = "weapon";

        // Bag definitions own a Bag layout.
        public const string Bag = "bag";

        // A protected quest item cannot be destroyed by the player.
        public const string QuestItem = "quest_item";
    }

    public static class ItemEquipmentSlotIds
    {
        // The equipped Bag activates its contents and adds its carry-capacity bonus.
        public const string Bag = "bag";
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
        public string BaseSecureContainerTierId;
        public ItemCharacterSettingsEntry Character;
        public ItemCatalogIdentityEntry[] Categories;
        public ItemCatalogIdentityEntry[] Tags;
        public ItemEquipmentSlotEntry[] EquipmentSlots;
        public ItemDefinitionAuthoringEntry[] Definitions;
        public SecureContainerTierAuthoringEntry[] SecureContainerTiers;
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

    // Ids that were released and removed. A retired id can never return with another
    // meaning, and GameService retires its rows once nothing references them.
    [Serializable]
    public sealed class ItemCatalogRetiredIdsEntry
    {
        public string[] Categories;
        public string[] Tags;
        public string[] EquipmentSlots;
        public string[] Definitions;
        public string[] SecureContainerTiers;
    }

    [Serializable]
    public sealed class ItemDefinitionAuthoringEntry
    {
        public string Id;
        public string DisplayName;
        public string Category;
        public string[] Tags;
        public long UnitWeight = -1;
        public int MaximumStackSize;
        public string[] EquipmentSlots;
        public string[] BlockedEquipmentSlots;
#pragma warning disable UAC1001 // Never Unity-serialized; null marks a missing authoring field.
        public bool? PlayerDestroyable;
#pragma warning restore UAC1001
        public ItemLocationEligibilityAuthoringEntry LocationEligibility;
        public string[] DefaultPolicies;
        public BagDefinitionAuthoringEntry Bag;
    }

    [Serializable]
    public sealed class ItemLocationEligibilityAuthoringEntry
    {
#pragma warning disable UAC1001 // Never Unity-serialized; null marks a missing authoring field.
        public bool? SecureContainer;
#pragma warning restore UAC1001
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

    [Serializable]
    public sealed class SecureContainerTierAuthoringEntry
    {
        public string Id;
        public string DisplayName;
        public int SlotCapacity;
    }

    [Serializable]
    public sealed class ItemCatalogRuntimeDocument
    {
        public int FormatVersion;
        public string CatalogId;
        public string Revision;
        public string BaseSecureContainerTierId;
        public ItemCharacterSettingsEntry Character;
        public ItemCatalogIdentityEntry[] Categories;
        public ItemCatalogIdentityEntry[] Tags;
        public ItemEquipmentSlotEntry[] EquipmentSlots;
        public ItemDefinition[] Definitions;
        public SecureContainerTierDefinition[] SecureContainerTiers;
        public ItemCatalogRetiredIdsEntry Retired;
    }

    [Serializable]
    public sealed class ItemDefinition
    {
        public string Id;
        public string DisplayName;
        public string Category;
        public string[] Tags;
        public long UnitWeight;
        public int MaximumStackSize;
        public string[] EquipmentSlots;
        public string[] BlockedEquipmentSlots;
        public bool PlayerDestroyable;
        public ItemLocationEligibility LocationEligibility;
        public string[] DefaultPolicies;
        public BagDefinition Bag;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class ItemLocationEligibility
    {
        public bool SecureContainer;
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
    public sealed class SecureContainerTierDefinition
    {
        public string Id;
        public string DisplayName;
        public int SlotCapacity;
        public string StructuralFingerprint;
    }
}
