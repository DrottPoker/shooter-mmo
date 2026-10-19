using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Items;

// What the worker knows about a character's committed items at one item-state revision:
// the carry state that movement uses and the occupied equipment slots that combat uses.
// Equipment is ordered by slot id, so two states at one revision compare exactly.
public sealed class CharacterItemState : IEquatable<CharacterItemState>
{
    private CharacterItemState(PlayerCarryState carry, SimulationEquippedItem[] equipment)
    {
        Carry = carry;
        Equipment = equipment;
    }

    public PlayerCarryState Carry { get; }

    public IReadOnlyList<SimulationEquippedItem> Equipment { get; }

    public long ItemStateRevision => Carry.ItemStateRevision;

    public static CharacterItemState Create(
        PlayerCarryState carry,
        IEnumerable<SimulationEquippedItem> equipment)
    {
        ArgumentNullException.ThrowIfNull(carry);
        ArgumentNullException.ThrowIfNull(equipment);
        var ordered = equipment
            .OrderBy(item => item.EquipmentSlotId, StringComparer.Ordinal)
            .ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(
                    ordered[index].EquipmentSlotId,
                    ordered[index - 1].EquipmentSlotId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException("An equipment slot holds at most one item.", nameof(equipment));
            }
        }

        return new CharacterItemState(carry, ordered);
    }

    public static CharacterItemState From(SimulationCharacterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Create(
            new PlayerCarryState(state.ItemStateRevision, state.CarriedWeight, state.CarryCapacity),
            state.Equipment);
    }

    public SimulationEquippedItem? FindEquipped(string equipmentSlotId)
    {
        foreach (var item in Equipment)
        {
            if (string.Equals(item.EquipmentSlotId, equipmentSlotId, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    public bool Equals(CharacterItemState? other)
    {
        if (other is null)
        {
            return false;
        }

        return ReferenceEquals(this, other)
            || (Carry.Equals(other.Carry) && Equipment.SequenceEqual(other.Equipment));
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as CharacterItemState);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Carry, Equipment.Count);
    }
}
