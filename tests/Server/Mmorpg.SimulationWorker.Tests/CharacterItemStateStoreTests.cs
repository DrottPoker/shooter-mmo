using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class CharacterItemStateStoreTests
{
    [Fact]
    public void StoreAppliesOnlyNewerRevisionsForTheActiveSession()
    {
        var store = new CharacterItemStateStore();
        var characterId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var initial = CharacterStateTestData.ItemState(new PlayerCarryState(4, 200, 200));

        Assert.Equal(
            ItemStateApplyResult.Applied,
            store.Register(characterId, sessionId, initial, out var registered));
        Assert.Same(initial, registered);

        var newer = CharacterStateTestData.ItemState(new PlayerCarryState(5, 210, 200));
        Assert.Equal(
            ItemStateApplyResult.Applied,
            store.ApplyCommitted(characterId, sessionId, newer, out var applied));
        Assert.Same(newer, applied);

        var stale = CharacterStateTestData.ItemState(new PlayerCarryState(4, 200, 200));
        Assert.Equal(
            ItemStateApplyResult.Stale,
            store.ApplyCommitted(characterId, sessionId, stale, out var preserved));
        Assert.Same(newer, preserved);
        Assert.True(store.TryGet(characterId, sessionId, out var current));
        Assert.Same(newer.Carry, current);
    }

    [Fact]
    public void StoreRejectsConflictingValuesAndStaleSessionUpdates()
    {
        var store = new CharacterItemStateStore();
        var characterId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        store.Register(
            characterId,
            sessionId,
            CharacterStateTestData.ItemState(new PlayerCarryState(7, 210, 200)),
            out _);

        Assert.Equal(
            ItemStateApplyResult.Conflict,
            store.ApplyCommitted(
                characterId,
                sessionId,
                CharacterStateTestData.ItemState(new PlayerCarryState(7, 220, 200)),
                out _));
        Assert.Equal(
            ItemStateApplyResult.SessionMismatch,
            store.ApplyCommitted(
                characterId,
                Guid.NewGuid(),
                CharacterStateTestData.ItemState(new PlayerCarryState(8, 220, 200)),
                out _));
        Assert.False(store.Remove(characterId, Guid.NewGuid()));
        Assert.True(store.Remove(characterId, sessionId));
        Assert.False(store.TryGet(characterId, sessionId, out _));
    }

    [Fact]
    public void EquipmentIsPartOfTheStateAtOneRevision()
    {
        var store = new CharacterItemStateStore();
        var characterId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var carry = new PlayerCarryState(3, 0, 200);
        var bow = new SimulationEquippedItem("main_hand", Guid.NewGuid(), "weapon.training_shortbow", 1, 1);
        var arrows = new SimulationEquippedItem("ammunition", Guid.NewGuid(), "ammunition.bronze_arrow", 40, 2);
        store.Register(characterId, sessionId, CharacterStateTestData.ItemState(carry, arrows, bow), out _);

        // The same equipment in another order is the same state.
        Assert.Equal(
            ItemStateApplyResult.Unchanged,
            store.ApplyCommitted(characterId, sessionId, CharacterStateTestData.ItemState(carry, bow, arrows), out _));
        // Another arrow count at the same revision contradicts it.
        Assert.Equal(
            ItemStateApplyResult.Conflict,
            store.ApplyCommitted(
                characterId,
                sessionId,
                CharacterStateTestData.ItemState(carry, bow, arrows with { Quantity = 39 }),
                out _));

        Assert.True(store.TryGetItemState(characterId, sessionId, out var state));
        Assert.Equal(new[] { "ammunition", "main_hand" }, state!.Equipment.Select(item => item.EquipmentSlotId));
        Assert.Equal(arrows, state.FindEquipped("ammunition"));
        Assert.Null(state.FindEquipped("off_hand"));
    }

    [Fact]
    public void AStateHoldsOneItemPerEquipmentSlot()
    {
        var carry = new PlayerCarryState(1, 0, 200);

        Assert.Throws<ArgumentException>(() => CharacterItemState.Create(
            carry,
            [
                new SimulationEquippedItem("main_hand", Guid.NewGuid(), "weapon.training_shortbow", 1, 1),
                new SimulationEquippedItem("main_hand", Guid.NewGuid(), "weapon.training_shortbow", 1, 1)
            ]));
    }
}
