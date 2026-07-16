using ShooterMmo.GameSimulation;
using SimulationWorker.Items;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class CarryStateStoreTests
{
    [Fact]
    public void StoreAppliesOnlyMonotonicCommittedRevisionsForTheExactSession()
    {
        var store = new CarryStateStore();
        var characterId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var initial = new PlayerCarryState(4, 200, 200);

        Assert.Equal(
            CarryStateApplyResult.Applied,
            store.Register(characterId, sessionId, initial, out var registered));
        Assert.Same(initial, registered);

        var newer = new PlayerCarryState(5, 210, 200);
        Assert.Equal(
            CarryStateApplyResult.Applied,
            store.ApplyCommitted(characterId, sessionId, newer, out var applied));
        Assert.Same(newer, applied);

        var stale = new PlayerCarryState(4, 200, 200);
        Assert.Equal(
            CarryStateApplyResult.Stale,
            store.ApplyCommitted(characterId, sessionId, stale, out var preserved));
        Assert.Same(newer, preserved);
        Assert.True(store.TryGet(characterId, sessionId, out var current));
        Assert.Same(newer, current);
    }

    [Fact]
    public void StoreRejectsConflictingValuesAndStaleSessionUpdates()
    {
        var store = new CarryStateStore();
        var characterId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        store.Register(
            characterId,
            sessionId,
            new PlayerCarryState(7, 210, 200),
            out _);

        Assert.Equal(
            CarryStateApplyResult.Conflict,
            store.ApplyCommitted(
                characterId,
                sessionId,
                new PlayerCarryState(7, 220, 200),
                out _));
        Assert.Equal(
            CarryStateApplyResult.SessionMismatch,
            store.ApplyCommitted(
                characterId,
                Guid.NewGuid(),
                new PlayerCarryState(8, 220, 200),
                out _));
        Assert.False(store.Remove(characterId, Guid.NewGuid()));
        Assert.True(store.Remove(characterId, sessionId));
        Assert.False(store.TryGet(characterId, sessionId, out _));
    }
}
