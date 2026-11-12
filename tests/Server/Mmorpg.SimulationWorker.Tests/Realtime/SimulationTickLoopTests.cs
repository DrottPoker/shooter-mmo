using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class SimulationTickLoopTests
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1000d / 30d);

    // The loop sorts the phases by their order, not by when they were registered, and hands
    // each the same tick: the new server tick and the players in entity id order.
    [Fact]
    public void ATickRunsThePhasesInTheirOrderWithThePlayersOfTheTick()
    {
        using var fixture = new RealtimeLoopFixture();
        var players = new[] { fixture.ConnectPlayer(1, "First"), fixture.ConnectPlayer(2, "Second") };
        using var metrics = new RealtimePerformanceMetrics();
        var phases = new SimulationTickPhases();
        var calls = new List<string>();
        phases.Add(SimulationTickOrder.Snapshots, "late", (in SimulationTick tick) =>
            calls.Add($"late {tick.ServerTick}"));
        phases.Add(SimulationTickOrder.Movement, "early", (in SimulationTick tick) =>
        {
            foreach (var player in tick.Players)
            {
                calls.Add($"early {tick.ServerTick} {player.NetworkEntityId}");
            }
        });
        var loop = new SimulationTickLoop(fixture.Clock, fixture.Entities, phases, metrics, NullLogger.Instance);
        var nextTick = TimeSpan.Zero;

        loop.ProcessDueTicks(nextTick, TickInterval, ref nextTick);

        Assert.Equal(
            [
                $"early 1 {players[0].Entity.NetworkEntityId}",
                $"early 1 {players[1].Entity.NetworkEntityId}",
                "late 1"
            ],
            calls);
        Assert.Equal(["early", "late"], loop.Phases.Select(phase => phase.Name));
    }

    // The loop runs at the tick rate for every player, so beyond its phases a tick must not
    // allocate: filling the players, building the tick, and calling the phases.
    [Fact]
    public void ATickAllocatesNothingBeyondItsPhases()
    {
        using var fixture = new RealtimeLoopFixture();
        for (var peerId = 1; peerId <= 50; peerId++)
        {
            fixture.ConnectPlayer(peerId, $"Player {peerId}");
        }

        using var metrics = new RealtimePerformanceMetrics();
        var phases = new SimulationTickPhases();
        var visited = 0L;
        phases.Add(SimulationTickOrder.Movement, "players", (in SimulationTick tick) =>
        {
            foreach (var player in tick.Players)
            {
                visited += (long)player.NetworkEntityId;
            }

            for (var index = 0; index < tick.Players.Count; index++)
            {
                visited += (long)tick.Players[index].NetworkEntityId;
            }
        });
        phases.Add(
            SimulationTickOrder.PlayerGrid,
            "player grid",
            static (in SimulationTick tick) => tick.PlayerGrid.Rebuild(tick.Players.AsReadOnlyList()));
        var loop = new SimulationTickLoop(fixture.Clock, fixture.Entities, phases, metrics, NullLogger.Instance);
        var nextTick = TimeSpan.Zero;
        RunTicks(loop, ref nextTick, 100);

        // A meter listener that another test left subscribed creates its metric points on
        // this thread once, so one quiet pass proves a steady tick.
        var allocated = new List<long>();
        for (var pass = 0; pass < 3 && !allocated.Contains(0); pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            RunTicks(loop, ref nextTick, 1_000);
            allocated.Add(GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Contains(0L, allocated);
        Assert.True(visited > 0);
    }

    // One due tick per call, so the loop never resynchronizes.
    private static void RunTicks(SimulationTickLoop loop, ref TimeSpan nextTick, int count)
    {
        for (var tick = 0; tick < count; tick++)
        {
            loop.ProcessDueTicks(nextTick, TickInterval, ref nextTick);
        }
    }
}
