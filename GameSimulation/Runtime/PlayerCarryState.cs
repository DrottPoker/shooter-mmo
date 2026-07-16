using System;
using ShooterMmo.WorldData.Items;

namespace ShooterMmo.GameSimulation
{
    public sealed class PlayerCarryState : IEquatable<PlayerCarryState>
    {
        public static readonly PlayerCarryState Default = new PlayerCarryState(
            0,
            0,
            PlayerEncumbranceRules.BaseCharacterCapacity);

        private readonly int movementMultiplierBasisPoints;

        public PlayerCarryState(
            long itemStateRevision,
            long carriedWeight,
            long carryCapacity)
        {
            if (itemStateRevision < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(itemStateRevision));
            }

            if (!PlayerEncumbranceRules.IsWithinHardCap(carriedWeight, carryCapacity))
            {
                throw new ArgumentException(
                    "Player carry state exceeds the authoritative hard cap.",
                    nameof(carriedWeight));
            }

            ItemStateRevision = itemStateRevision;
            CarriedWeight = carriedWeight;
            CarryCapacity = carryCapacity;
            movementMultiplierBasisPoints =
                PlayerEncumbranceRules.CalculateMovementMultiplierBasisPoints(
                    carriedWeight,
                    carryCapacity);
        }

        public long ItemStateRevision { get; }

        public long CarriedWeight { get; }

        public long CarryCapacity { get; }

        public bool SprintAllowed
        {
            get { return CarriedWeight <= CarryCapacity; }
        }

        public int MovementMultiplierBasisPoints
        {
            get { return movementMultiplierBasisPoints; }
        }

        public float MovementMultiplier
        {
            get
            {
                return movementMultiplierBasisPoints
                    / (float)PlayerEncumbranceRules.BaseMovementMultiplierBasisPoints;
            }
        }

        public bool Equals(PlayerCarryState other)
        {
            return other != null
                && ItemStateRevision == other.ItemStateRevision
                && CarriedWeight == other.CarriedWeight
                && CarryCapacity == other.CarryCapacity;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as PlayerCarryState);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = ItemStateRevision.GetHashCode();
                hashCode = (hashCode * 397) ^ CarriedWeight.GetHashCode();
                hashCode = (hashCode * 397) ^ CarryCapacity.GetHashCode();
                return hashCode;
            }
        }
    }

    public static class PlayerEncumbranceRules
    {
        public const long BaseCharacterCapacity = CarryWeightDefaults.BaseCharacterCapacity;
        public const int BaseMovementMultiplierBasisPoints =
            EncumbranceRules.BaseMovementMultiplierBasisPoints;
        public const int MinimumMovementMultiplierBasisPoints =
            EncumbranceRules.MinimumMovementMultiplierBasisPoints;

        public static bool IsWithinHardCap(long carriedWeight, long carryCapacity)
        {
            return EncumbranceRules.IsWithinHardCap(carriedWeight, carryCapacity);
        }

        public static int CalculateMovementMultiplierBasisPoints(
            long carriedWeight,
            long carryCapacity)
        {
            return EncumbranceRules.CalculateMovementMultiplierBasisPoints(
                carriedWeight,
                carryCapacity);
        }
    }
}
