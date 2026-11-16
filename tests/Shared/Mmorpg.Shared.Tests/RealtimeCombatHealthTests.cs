using Mmorpg.GameProtocol;

namespace Mmorpg.Shared.Tests;

// The health rule every receiver of a combatant state follows: the state's health plus one
// regeneration amount per step reached, a step every second from the named tick, up to the
// maximum.
public sealed class RealtimeCombatHealthTests
{
    private const int TickRate = 30;

    [Fact]
    public void EveryStepAddsTheAmountFromItsTickOnUntilTheMaximum()
    {
        var state = State(health: 40, amount: 7, tick: 1_000);

        Assert.Equal(40, RealtimeCombatHealth.At(state, 999, TickRate));
        Assert.Equal(47, RealtimeCombatHealth.At(state, 1_000, TickRate));
        Assert.Equal(47, RealtimeCombatHealth.At(state, 1_029, TickRate));
        Assert.Equal(54, RealtimeCombatHealth.At(state, 1_030, TickRate));
        Assert.Equal(82, RealtimeCombatHealth.At(state, 1_150, TickRate));
        Assert.Equal(96, RealtimeCombatHealth.At(state, 1_239, TickRate));
        Assert.Equal(100, RealtimeCombatHealth.At(state, 1_240, TickRate));
        Assert.Equal(100, RealtimeCombatHealth.At(state, 1_000_000, TickRate));
    }

    // Without an amount, alive state, or tick rate the state's own health stands.
    [Fact]
    public void AStateThatRegeneratesNothingKeepsItsHealth()
    {
        Assert.Equal(40, RealtimeCombatHealth.At(State(health: 40, amount: 0, tick: 0), 5_000, TickRate));
        Assert.Equal(
            0,
            RealtimeCombatHealth.At(State(health: 0, amount: 0, tick: 0, lifeState: RealtimeCombatLifeState.Dead), 5_000, TickRate));
        Assert.Equal(
            40,
            RealtimeCombatHealth.At(State(health: 40, amount: 5, tick: 100, lifeState: RealtimeCombatLifeState.Evading), 5_000, TickRate));
        Assert.Equal(40, RealtimeCombatHealth.At(State(health: 40, amount: 5, tick: 100), 5_000, 0));
        Assert.Equal(40, RealtimeCombatHealth.At(State(health: 40, amount: 5, tick: 100), 5_000, -30));
        Assert.Equal(100, RealtimeCombatHealth.At(State(health: 100, amount: 5, tick: 100), 5_000, TickRate));
        Assert.Throws<ArgumentNullException>(() => RealtimeCombatHealth.At(null!, 5_000, TickRate));
    }

    // The server tick wraps, so a step tick shortly before the wrap is reached by a tick after
    // it, and a tick far ahead counts as not reached yet.
    [Fact]
    public void TicksCompareAcrossTheWrap()
    {
        var state = State(health: 10, amount: 2, tick: uint.MaxValue - 9);

        Assert.Equal(10, RealtimeCombatHealth.At(state, uint.MaxValue - 10, TickRate));
        Assert.Equal(12, RealtimeCombatHealth.At(state, uint.MaxValue - 9, TickRate));
        Assert.Equal(12, RealtimeCombatHealth.At(state, 19, TickRate));
        Assert.Equal(14, RealtimeCombatHealth.At(state, 20, TickRate));
        Assert.Equal(10, RealtimeCombatHealth.At(State(health: 10, amount: 2, tick: 20), uint.MaxValue - 9, TickRate));
        Assert.Equal(10, RealtimeCombatHealth.At(State(health: 10, amount: 2, tick: 2_000_000_000), 0, TickRate));
    }

    // The largest numbers the protocol carries stay within the maximum without overflowing.
    [Fact]
    public void LargeAmountsAndLongWaitsStopAtTheMaximum()
    {
        var state = new RealtimeCombatantState(
            7,
            1,
            1,
            int.MaxValue,
            RealtimeCombatLifeState.Alive,
            false,
            0,
            0,
            int.MaxValue,
            0);

        Assert.Equal(int.MaxValue, RealtimeCombatHealth.At(state, int.MaxValue, 1));
        Assert.Equal(int.MaxValue, RealtimeCombatHealth.At(state, 0, TickRate));
    }

    private static RealtimeCombatantState State(
        int health,
        int amount,
        uint tick,
        RealtimeCombatLifeState lifeState = RealtimeCombatLifeState.Alive)
    {
        return new RealtimeCombatantState(7, 3, health, 100, lifeState, false, 0, 0, amount, tick);
    }
}
