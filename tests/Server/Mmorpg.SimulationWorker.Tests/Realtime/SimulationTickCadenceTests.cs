using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Which ticks the periodic passes take: the snapshots, the tick before them, the lanes, and the
// free ticks, at the worker's rates and at others.
public sealed class SimulationTickCadenceTests
{
    // At 30 ticks and 15 snapshots per second the snapshots take the even ticks and the interest
    // refresh the odd ones; corpse presence, twice a second, and the interaction revalidation,
    // five times a second, run on odd ticks that they do not share, each run at most one tick
    // after the tick its period names, and as often as before.
    [Fact]
    public void TheWorkersLanesKeepOffTheSnapshotTicksAndOffEachOther()
    {
        var cadence = new SimulationTickCadence(30, 15);
        var corpses = cadence.AddLane("corpse presence", 15);
        var revalidation = cadence.AddLane("interaction revalidation", 6);

        for (var tick = 1u; tick <= 600; tick++)
        {
            Assert.Equal(tick % 2 == 0, cadence.IsSnapshotTick(tick));
            Assert.Equal(tick % 2 == 1, cadence.IsBeforeSnapshotTick(tick));
            Assert.False(corpses.IsDue(tick) && cadence.IsSnapshotTick(tick));
            Assert.False(revalidation.IsDue(tick) && cadence.IsSnapshotTick(tick));
            Assert.False(corpses.IsDue(tick) && revalidation.IsDue(tick));
        }

        Assert.Equal(40, Runs(corpses, 600).Count);
        Assert.Equal(100, Runs(revalidation, 600).Count);
        AssertWithinOneTickOfItsPeriod(corpses, 600);
        AssertWithinOneTickOfItsPeriod(revalidation, 600);
        Assert.Equal([1u, 15u, 31u, 45u], Runs(corpses, 60).Take(4));
        Assert.Equal([5u, 11u, 17u, 23u, 29u], Runs(revalidation, 30));
    }

    // With three ticks per snapshot a lane prefers the tick that the interest refresh leaves free.
    [Fact]
    public void ALaneKeepsOffTheInterestTickWhereAnotherTickIsFree()
    {
        var cadence = new SimulationTickCadence(30, 10);
        var lane = cadence.AddLane("revalidation", 6);

        var runs = Runs(lane, 60);

        Assert.Equal(10, runs.Count);
        Assert.All(runs, tick =>
        {
            Assert.False(cadence.IsSnapshotTick(tick));
            Assert.False(cadence.IsBeforeSnapshotTick(tick));
        });
    }

    // With one snapshot per tick every tick is a snapshot tick: the interest refresh runs on every
    // tick, and the lanes keep the ticks their periods name and still keep off each other.
    [Fact]
    public void WithASnapshotEveryTickTheLanesKeepTheirOwnTicks()
    {
        var cadence = new SimulationTickCadence(20, 20);
        var first = cadence.AddLane("first", 4);
        var second = cadence.AddLane("second", 4);

        for (var tick = 1u; tick <= 40; tick++)
        {
            Assert.True(cadence.IsSnapshotTick(tick));
            Assert.True(cadence.IsBeforeSnapshotTick(tick));
            Assert.False(first.IsDue(tick) && second.IsDue(tick));
        }

        Assert.Equal(10, Runs(first, 40).Count);
        Assert.Equal(10, Runs(second, 40).Count);
        Assert.True(cadence.IsFree(3));
    }

    // A pass without a fixed period, such as the character checkpoint, waits for a tick that no
    // snapshot and no lane takes, but no longer than a second.
    [Fact]
    public void AFreeTickHasNoSnapshotAndNoLaneAndAPassWaitsForOneAtMostASecond()
    {
        var cadence = new SimulationTickCadence(30, 15);
        cadence.AddLane("corpse presence", 15);
        cadence.AddLane("interaction revalidation", 6);

        var free = Enumerable.Range(1, 30).Select(tick => (uint)tick).Where(cadence.IsFree).ToArray();

        Assert.Equal([3u, 7u, 9u, 13u, 19u, 21u, 25u, 27u], free);
        Assert.False(cadence.IsFreeFor(31, 30));
        Assert.True(cadence.IsFreeFor(33, 30));
        Assert.True(cadence.IsFreeFor(60, 30));
    }

    // A pass every tick has no lane to keep and runs on every tick.
    [Fact]
    public void APassEveryTickRunsOnEveryTick()
    {
        var cadence = new SimulationTickCadence(30, 15);
        var lane = cadence.AddLane("every tick", 1);

        Assert.Equal(30, Runs(lane, 30).Count);
    }

    [Fact]
    public void ASnapshotRateThatDoesNotDivideTheTickRateFails()
    {
        Assert.Throws<ArgumentException>(() => new SimulationTickCadence(30, 20));
    }

    private static List<uint> Runs(SimulationTickLane lane, int ticks)
    {
        return [.. Enumerable.Range(1, ticks).Select(tick => (uint)tick).Where(lane.IsDue)];
    }

    // Every run is on the tick its period names or on the tick after it.
    private static void AssertWithinOneTickOfItsPeriod(SimulationTickLane lane, int ticks)
    {
        foreach (var tick in Runs(lane, ticks))
        {
            var place = tick % (uint)lane.PeriodTicks;
            Assert.True(place == lane.Offset || (tick - 1) % (uint)lane.PeriodTicks == lane.Offset, $"{lane.Name} ran on {tick}.");
        }
    }
}
