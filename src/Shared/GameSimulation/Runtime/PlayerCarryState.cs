using System;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameSimulation
{
    public sealed class PlayerCarryState : IEquatable<PlayerCarryState>
    {
        // The state before GameService delivers a committed one: nothing carried, full speed.
        // Base carry capacity is item catalog content, so the placeholder capacity is 1.
        public static readonly PlayerCarryState Default = new PlayerCarryState(0, 0, 1);

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

            // Weight above the hard cap is valid: death can leave an involuntary overflow,
            // which moves at the minimum multiplier until the character sheds weight.
            if (carriedWeight < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(carriedWeight));
            }

            if (carryCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(carryCapacity));
            }

            ItemStateRevision = itemStateRevision;
            CarriedWeight = carriedWeight;
            CarryCapacity = carryCapacity;
            movementMultiplierBasisPoints =
                EncumbranceRules.CalculateMovementMultiplierBasisPoints(
                    carriedWeight,
                    carryCapacity);
        }

        public long ItemStateRevision { get; }

        public long CarriedWeight { get; }

        public long CarryCapacity { get; }

        public int MovementMultiplierBasisPoints
        {
            get { return movementMultiplierBasisPoints; }
        }

        public float MovementMultiplier
        {
            get
            {
                return (float)(movementMultiplierBasisPoints
                    / (float)EncumbranceRules.BaseMovementMultiplierBasisPoints);
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
}
