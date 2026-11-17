using System.Diagnostics;
using Mmorpg.SimulationWorker.Entities;

namespace Mmorpg.SimulationWorker.Realtime;

// Brings the interest index to the players and world actors as they are and refreshes the
// visibility of the connections whose turn it is, sending their spawns and despawns, on the tick
// before each snapshot tick, so the snapshot right after it carries every entity that came into
// view and none that left. Without a player it only drops the world actor tombstones that no
// connection sees. It times its parts: the index, the visibility refresh, and the spawns and
// despawns it sent. Loop-confined.
internal sealed class InterestRefresher(
    SimulationEntityRegistry entityRegistry,
    EntityPresenceReplicator presence,
    RealtimePerformanceMetrics performanceMetrics)
{
    public const string PhaseName = "interest";

    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly RealtimePerformanceMetrics.DurationTimer indexTimer =
        performanceMetrics.TickPhasePartTimer(PhaseName, "index");
    private readonly RealtimePerformanceMetrics.DurationTimer refreshTimer =
        performanceMetrics.TickPhasePartTimer(PhaseName, "refresh");
    private readonly RealtimePerformanceMetrics.DurationTimer presenceTimer =
        performanceMetrics.TickPhasePartTimer(PhaseName, "spawns and despawns");

    public void AddTickPhase(SimulationTickPhases phases, SimulationTickCadence cadence)
    {
        ArgumentNullException.ThrowIfNull(cadence);
        phases.AddPeriodic(
            SimulationTickOrder.Interest,
            PhaseName,
            (in SimulationTick tick) =>
            {
                if (!cadence.IsBeforeSnapshotTick(tick.ServerTick))
                {
                    return false;
                }

                Refresh();
                return true;
            });
    }

    public void Refresh()
    {
        entityRegistry.CopyPlayersTo(playerBuffer);
        if (playerBuffer.Count == 0)
        {
            // No player has interest to refresh, but despawned world actors must still be
            // pruned, or their tombstones stay until a player joins.
            presence.PruneDespawned();
            return;
        }

        var started = Stopwatch.GetTimestamp();
        presence.SynchronizeInterestIndex(playerBuffer);
        var refreshStarted = Stopwatch.GetTimestamp();
        indexTimer.Record(Stopwatch.GetElapsedTime(started, refreshStarted));
        var presenceTicks = presence.RefreshInterests();
        var ended = Stopwatch.GetTimestamp();
        refreshTimer.Record(Stopwatch.GetElapsedTime(0, ended - refreshStarted - presenceTicks));
        presenceTimer.Record(Stopwatch.GetElapsedTime(0, presenceTicks));
        performanceMetrics.RecordInterestRefresh(Stopwatch.GetElapsedTime(started, ended));
    }
}
