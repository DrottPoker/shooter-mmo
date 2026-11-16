namespace Mmorpg.SimulationWorker.Realtime;

// Decides on which ticks the passes that do not run every tick do their work, so a snapshot tick
// carries the snapshots and no other periodic pass:
// - The snapshots go out on the snapshot ticks, every SnapshotIntervalTicks ticks.
// - The interest refresh and the changed looks run on the tick before each snapshot tick, so the
//   snapshot right after them carries what they found.
// - Every other pass with a fixed period takes a lane: a place in its period chosen once, at
//   composition, that keeps it off the ticks of the lanes taken before it and, where another tick
//   is free, off the interest tick. A run that would land on a snapshot tick moves to the tick
//   after it, so it moves by one tick at most.
// - A pass whose due tick moves with its own runs, the character checkpoint, waits for a free
//   tick, at most one second.
// With one snapshot per tick every tick is a snapshot tick, and the passes keep their own ticks.
// Composition fills it; the loop only reads it, without allocating.
internal sealed class SimulationTickCadence
{
    // Longer hyperperiods only keep the lanes off the snapshot ticks.
    private const long MaximumHyperperiodTicks = 1L << 16;

    private readonly List<SimulationTickLane> lanes = [];
    private readonly uint snapshotInterval;
    private readonly uint longestFreeTickWait;

    public SimulationTickCadence(int tickRateHz, int snapshotRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tickRateHz, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshotRateHz, 1);
        if (snapshotRateHz > tickRateHz || tickRateHz % snapshotRateHz != 0)
        {
            throw new ArgumentException("The snapshot rate must divide the tick rate.", nameof(snapshotRateHz));
        }

        SnapshotIntervalTicks = tickRateHz / snapshotRateHz;
        snapshotInterval = (uint)SnapshotIntervalTicks;
        longestFreeTickWait = (uint)tickRateHz;
    }

    public int SnapshotIntervalTicks { get; }

    // The lanes in the order they were taken.
    public IReadOnlyList<SimulationTickLane> Lanes => lanes;

    public bool IsSnapshotTick(uint tick)
    {
        return tick % snapshotInterval == 0;
    }

    // The tick before each snapshot tick: the interest refresh and the changed looks. With one
    // snapshot per tick it is every tick, and those phases run before the snapshots.
    public bool IsBeforeSnapshotTick(uint tick)
    {
        return unchecked(tick + 1u) % snapshotInterval == 0;
    }

    // Takes a lane for a pass every periodTicks ticks: of the places in its period, the one that
    // shares the fewest ticks with the lanes taken before it, then with the interest tick where
    // another tick is free, then that moves the fewest runs off a snapshot tick, then the first.
    public SimulationTickLane AddLane(string name, int periodTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(periodTicks, 1);
        var hyperperiod = Hyperperiod(periodTicks);
        SimulationTickLane? best = null;
        var bestCost = long.MaxValue;
        for (var offset = 0; offset < periodTicks; offset++)
        {
            var candidate = new SimulationTickLane(name, periodTicks, offset, SnapshotIntervalTicks);
            var cost = CostOf(candidate, hyperperiod);
            if (cost < bestCost)
            {
                best = candidate;
                bestCost = cost;
            }
        }

        lanes.Add(best!);
        return best!;
    }

    // Whether no other periodic pass takes the tick, for a pass that waits for one: no snapshot
    // unless every tick has one, no lane, and not the interest tick unless every tick without a
    // snapshot is one. A pass due at dueTick takes the tick anyway once it waited a second.
    public bool IsFreeFor(uint tick, uint dueTick)
    {
        return IsFree(tick) || unchecked(tick - dueTick) >= longestFreeTickWait;
    }

    public bool IsFree(uint tick)
    {
        if (snapshotInterval > 1 && IsSnapshotTick(tick))
        {
            return false;
        }

        if (snapshotInterval > 2 && IsBeforeSnapshotTick(tick))
        {
            return false;
        }

        foreach (var lane in lanes)
        {
            if (lane.IsDue(tick))
            {
                return false;
            }
        }

        return true;
    }

    // The ticks after which the snapshots and every lane repeat, or 0 when that is too long to
    // compare the lanes over.
    private long Hyperperiod(int periodTicks)
    {
        var hyperperiod = LeastCommonMultiple(snapshotInterval, periodTicks);
        foreach (var lane in lanes)
        {
            if (hyperperiod > MaximumHyperperiodTicks)
            {
                return 0;
            }

            hyperperiod = LeastCommonMultiple(hyperperiod, lane.PeriodTicks);
        }

        return hyperperiod > MaximumHyperperiodTicks ? 0 : hyperperiod;
    }

    // A tick shared with an earlier lane weighs more than all ticks shared with the interest
    // tick, which weigh more than all runs moved off a snapshot tick. Without a hyperperiod only
    // a place on a snapshot tick costs.
    private long CostOf(SimulationTickLane candidate, long hyperperiod)
    {
        if (hyperperiod == 0)
        {
            return snapshotInterval > 1 && candidate.Offset % snapshotInterval == 0 ? 1 : 0;
        }

        long sharedWithLanes = 0;
        long sharedWithInterest = 0;
        long moved = 0;

        // The second hyperperiod, so no tick before it wraps around zero.
        for (var tick = (uint)hyperperiod; tick < 2 * hyperperiod; tick++)
        {
            if (!candidate.IsDue(tick))
            {
                continue;
            }

            if (snapshotInterval > 1 && tick % (uint)candidate.PeriodTicks != candidate.Offset)
            {
                moved++;
            }

            if (snapshotInterval > 2 && IsBeforeSnapshotTick(tick))
            {
                sharedWithInterest++;
            }

            foreach (var lane in lanes)
            {
                if (lane.IsDue(tick))
                {
                    sharedWithLanes++;
                    break;
                }
            }
        }

        var scale = hyperperiod + 1;
        return (((sharedWithLanes * scale) + sharedWithInterest) * scale) + moved;
    }

    private static long LeastCommonMultiple(long first, long second)
    {
        var a = first;
        var b = second;
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return first / a * second;
    }
}

// The ticks of one periodic pass: every PeriodTicks ticks at Offset in its period, a run that
// lands on a snapshot tick on the tick after it. A pass every tick runs on every tick.
internal sealed class SimulationTickLane
{
    private readonly uint period;
    private readonly uint offset;
    private readonly uint snapshotInterval;

    public SimulationTickLane(string name, int periodTicks, int offset, int snapshotIntervalTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(periodTicks, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, periodTicks);
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshotIntervalTicks, 1);
        Name = name;
        PeriodTicks = periodTicks;
        Offset = (uint)offset;
        period = (uint)periodTicks;
        this.offset = (uint)offset;
        snapshotInterval = (uint)snapshotIntervalTicks;
    }

    public string Name { get; }

    public int PeriodTicks { get; }

    public uint Offset { get; }

    public bool IsDue(uint tick)
    {
        if (period == 1)
        {
            return true;
        }

        if (tick % period == offset)
        {
            return snapshotInterval == 1 || tick % snapshotInterval != 0;
        }

        if (snapshotInterval == 1)
        {
            return false;
        }

        var previous = unchecked(tick - 1u);
        return previous % period == offset && previous % snapshotInterval == 0;
    }
}
