using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShooterMmo.WorldData.Items
{
    public sealed class ItemStackState
    {
        public ItemStackState(
            string definitionId,
            long quantity,
            string effectivePolicyFingerprint,
            string stackStateFingerprint)
        {
            DefinitionId = definitionId;
            Quantity = quantity;
            EffectivePolicyFingerprint = effectivePolicyFingerprint;
            StackStateFingerprint = stackStateFingerprint;
        }

        public string DefinitionId { get; }

        public long Quantity { get; }

        public string EffectivePolicyFingerprint { get; }

        public string StackStateFingerprint { get; }
    }

    public static class ItemStackRules
    {
        public static bool AreCompatible(ItemStackState left, ItemStackState right)
        {
            return IsValid(left)
                && IsValid(right)
                && string.Equals(left.DefinitionId, right.DefinitionId, StringComparison.Ordinal)
                && string.Equals(
                    left.EffectivePolicyFingerprint,
                    right.EffectivePolicyFingerprint,
                    StringComparison.Ordinal)
                && string.Equals(
                    left.StackStateFingerprint,
                    right.StackStateFingerprint,
                    StringComparison.Ordinal);
        }

        public static bool CanMerge(
            ItemDefinition definition,
            ItemStackState left,
            ItemStackState right)
        {
            if (definition == null
                || definition.MaximumStackSize <= 0
                || !AreCompatible(left, right)
                || !string.Equals(definition.Id, left.DefinitionId, StringComparison.Ordinal)
                || left.Quantity > definition.MaximumStackSize
                || right.Quantity > definition.MaximumStackSize)
            {
                return false;
            }

            return left.Quantity <= definition.MaximumStackSize - right.Quantity;
        }

        private static bool IsValid(ItemStackState state)
        {
            return state != null
                && !string.IsNullOrWhiteSpace(state.DefinitionId)
                && state.Quantity > 0
                && !string.IsNullOrWhiteSpace(state.EffectivePolicyFingerprint)
                && !string.IsNullOrWhiteSpace(state.StackStateFingerprint);
        }
    }

    public static class ItemSlotRules
    {
        public static bool Accepts(ItemDefinition definition, BagSlotDefinition slot)
        {
            if (definition == null || slot == null)
            {
                return false;
            }

            if (string.Equals(slot.Kind, BagSlotKindIds.General, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.Equals(
                    slot.Kind,
                    BagSlotKindIds.Specialized,
                    StringComparison.Ordinal)
                || definition.Tags == null
                || slot.AcceptedTags == null)
            {
                return false;
            }

            for (var acceptedIndex = 0; acceptedIndex < slot.AcceptedTags.Length; acceptedIndex++)
            {
                for (var tagIndex = 0; tagIndex < definition.Tags.Length; tagIndex++)
                {
                    if (string.Equals(
                        slot.AcceptedTags[acceptedIndex],
                        definition.Tags[tagIndex],
                        StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    public static class ItemEquipmentRules
    {
        public static bool IsCompatible(ItemDefinition definition, string equipmentSlotId)
        {
            if (definition == null
                || definition.EquipmentSlots == null
                || string.IsNullOrWhiteSpace(equipmentSlotId))
            {
                return false;
            }

            for (var index = 0; index < definition.EquipmentSlots.Length; index++)
            {
                if (string.Equals(
                    definition.EquipmentSlots[index],
                    equipmentSlotId,
                    StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static class SecureContainerRules
    {
        public static bool IsEligible(ItemDefinition definition)
        {
            return definition != null
                && !string.Equals(
                    definition.Category,
                    ItemCategoryIds.Weapon,
                    StringComparison.Ordinal)
                && definition.LocationEligibility != null
                && definition.LocationEligibility.SecureContainer;
        }
    }

    public sealed class ItemPolicyState
    {
        public ItemPolicyState(
            string policyKind,
            string status,
            string sourceKind,
            string sourceId)
        {
            PolicyKind = policyKind;
            Status = status;
            SourceKind = sourceKind;
            SourceId = sourceId;
        }

        public string PolicyKind { get; }

        public string Status { get; }

        public string SourceKind { get; }

        public string SourceId { get; }
    }

    public enum ItemDeathDisposition
    {
        Lootable = 1,
        ProtectedRecovery = 2,
        InsuredRecovery = 3
    }

    public sealed class ItemPolicyCapabilities
    {
        public ItemPolicyCapabilities(
            bool canTrade,
            bool canListOnAuction,
            bool canSellToVendor,
            bool canPlayerDestroy,
            ItemDeathDisposition deathDisposition,
            bool canStack)
        {
            CanTrade = canTrade;
            CanListOnAuction = canListOnAuction;
            CanSellToVendor = canSellToVendor;
            CanPlayerDestroy = canPlayerDestroy;
            DeathDisposition = deathDisposition;
            CanStack = canStack;
        }

        public bool CanTrade { get; }

        public bool CanListOnAuction { get; }

        public bool CanSellToVendor { get; }

        public bool CanPlayerDestroy { get; }

        public ItemDeathDisposition DeathDisposition { get; }

        public bool CanStack { get; }
    }

    public static class ItemPolicyRules
    {
        public const string ActiveStatus = "active";

        public static ItemPolicyCapabilities Evaluate(
            ItemDefinition definition,
            IEnumerable<ItemPolicyState> policies)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (policies == null)
            {
                throw new ArgumentNullException(nameof(policies));
            }

            var hasProtectedPolicy = false;
            var hasInsurancePolicy = false;
            foreach (var policy in policies)
            {
                if (policy == null
                    || !string.Equals(policy.Status, ActiveStatus, StringComparison.Ordinal))
                {
                    continue;
                }

                hasProtectedPolicy |= string.Equals(
                    policy.PolicyKind,
                    ItemPolicyIds.ProtectedOnDeath,
                    StringComparison.Ordinal);
                hasInsurancePolicy |= string.Equals(
                    policy.PolicyKind,
                    ItemPolicyIds.Insured,
                    StringComparison.Ordinal);
            }

            var transferAllowed = !hasProtectedPolicy && !hasInsurancePolicy;
            var protectedQuestItem = hasProtectedPolicy
                && string.Equals(
                    definition.Category,
                    ItemCategoryIds.QuestItem,
                    StringComparison.Ordinal);
            var deathDisposition = hasProtectedPolicy
                ? ItemDeathDisposition.ProtectedRecovery
                : hasInsurancePolicy
                    ? ItemDeathDisposition.InsuredRecovery
                    : ItemDeathDisposition.Lootable;

            return new ItemPolicyCapabilities(
                transferAllowed,
                transferAllowed,
                transferAllowed,
                definition.PlayerDestroyable && !protectedQuestItem,
                deathDisposition,
                definition.MaximumStackSize > 1 && !hasInsurancePolicy);
        }

        public static bool CanApplyInsurance(ItemDefinition definition)
        {
            return definition != null
                && definition.MaximumStackSize == 1
                && definition.EquipmentSlots != null
                && definition.EquipmentSlots.Length > 0;
        }
    }

    public enum BagDestinationKind
    {
        PermanentInventoryGeneralSlot = 1,
        BagGeneralSlot = 2,
        BagSpecializedSlot = 3,
        BankGeneralSlot = 4,
        RecoveryStorageSlot = 5,
        SecureContainerSlot = 6,
        CharacterBagEquipmentSlot = 7,
        CorpseBagEquipmentSlot = 8
    }

    public static class BagLocationRules
    {
        public static bool CanPlace(
            bool hasContents,
            BagDestinationKind destination,
            bool wouldCreateContainmentCycle)
        {
            if (wouldCreateContainmentCycle)
            {
                return false;
            }

            if (hasContents)
            {
                return destination == BagDestinationKind.CharacterBagEquipmentSlot
                    || destination == BagDestinationKind.CorpseBagEquipmentSlot;
            }

            return destination == BagDestinationKind.PermanentInventoryGeneralSlot
                || destination == BagDestinationKind.BagGeneralSlot
                || destination == BagDestinationKind.BankGeneralSlot
                || destination == BagDestinationKind.RecoveryStorageSlot
                || destination == BagDestinationKind.CharacterBagEquipmentSlot
                || destination == BagDestinationKind.CorpseBagEquipmentSlot;
        }
    }

    public static class ItemWeightRules
    {
        public static long CalculateStackWeight(long unitWeight, long quantity)
        {
            if (unitWeight < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(unitWeight));
            }

            if (quantity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(quantity));
            }

            return checked(unitWeight * quantity);
        }

        public static long SumWeight(IEnumerable<long> weights)
        {
            if (weights == null)
            {
                throw new ArgumentNullException(nameof(weights));
            }

            var total = 0L;
            foreach (var weight in weights)
            {
                if (weight < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(weights));
                }

                total = checked(total + weight);
            }

            return total;
        }
    }

    public static class CarryWeightDefaults
    {
        public const long BaseCharacterCapacity = 200;
    }

    public static class EncumbranceRules
    {
        public const int BaseMovementMultiplierBasisPoints = 10000;
        public const int MinimumMovementMultiplierBasisPoints = 2000;

        public static bool IsWithinHardCap(long carriedWeight, long capacity)
        {
            ValidateWeights(carriedWeight, capacity);

            var extraCapacity = ((capacity / 5) * 2)
                + (((capacity % 5) * 2) / 5);
            if (capacity > long.MaxValue - extraCapacity)
            {
                return true;
            }

            return carriedWeight <= capacity + extraCapacity;
        }

        public static int CalculateMovementMultiplierBasisPoints(
            long carriedWeight,
            long capacity)
        {
            ValidateWeights(carriedWeight, capacity);
            if (carriedWeight <= capacity)
            {
                return BaseMovementMultiplierBasisPoints;
            }

            if (!IsWithinHardCap(carriedWeight, capacity))
            {
                return MinimumMovementMultiplierBasisPoints;
            }

            var numerator = (((BigInteger)capacity * 3)
                - ((BigInteger)carriedWeight * 2))
                * BaseMovementMultiplierBasisPoints;
            var result = (int)(numerator / capacity);
            if (result < MinimumMovementMultiplierBasisPoints)
            {
                return MinimumMovementMultiplierBasisPoints;
            }

            return result > BaseMovementMultiplierBasisPoints
                ? BaseMovementMultiplierBasisPoints
                : result;
        }

        private static void ValidateWeights(long carriedWeight, long capacity)
        {
            if (carriedWeight < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(carriedWeight));
            }

            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
        }
    }
}
