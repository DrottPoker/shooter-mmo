using System;

namespace ShooterMmo.WorldData.Items
{
    public static class ItemCatalogFormat
    {
        public const int Version = 2;
    }

    public static class ItemCategoryIds
    {
        public const string Material = "material";
        public const string Medical = "medical";
        public const string Weapon = "weapon";
        public const string Ammunition = "ammunition";
        public const string Armor = "armor";
        public const string Tool = "tool";
        public const string Bag = "bag";
        public const string QuestItem = "quest_item";
    }

    public static class ItemTagIds
    {
        public const string Medical = "medical";
        public const string Material = "material";
        public const string Ammunition = "ammunition";
    }

    public static class ItemEquipmentSlotIds
    {
        public const string Head = "head";
        public const string BodyArmor = "body_armor";
        public const string PrimaryWeapon = "primary_weapon";
        public const string SecondaryWeapon = "secondary_weapon";
        public const string Tool = "tool";
        public const string Ring1 = "ring_1";
        public const string Ring2 = "ring_2";
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
        public ItemCatalogIdentityEntry[] Categories;
        public ItemCatalogIdentityEntry[] Tags;
        public ItemCatalogIdentityEntry[] EquipmentSlots;
        public ItemDefinitionAuthoringEntry[] Definitions;
        public SecureContainerTierAuthoringEntry[] SecureContainerTiers;
    }

    [Serializable]
    public sealed class ItemCatalogIdentityEntry
    {
        public string Id;
        public string DisplayName;
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
        public bool? PlayerDestroyable;
        public ItemLocationEligibilityAuthoringEntry LocationEligibility;
        public string[] DefaultPolicies;
        public BagDefinitionAuthoringEntry Bag;
    }

    [Serializable]
    public sealed class ItemLocationEligibilityAuthoringEntry
    {
        public bool? SecureContainer;
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
        public ItemCatalogIdentityEntry[] Categories;
        public ItemCatalogIdentityEntry[] Tags;
        public ItemCatalogIdentityEntry[] EquipmentSlots;
        public ItemDefinition[] Definitions;
        public SecureContainerTierDefinition[] SecureContainerTiers;
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
