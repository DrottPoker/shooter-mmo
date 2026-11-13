namespace Mmorpg.Tools.StackStressGenerator;

public enum PlayingCounter
{
    // Mobs a bot killed, by the Died event that names it the killer.
    Kills,

    // Damage that Mobs dealt the bots.
    MobDamageTaken,

    // Deaths of the bots.
    BotDeaths,

    // Bots that walked back to their own corpse and looted it back.
    CorpsesRecovered,

    // Bots whose corpse was gone or out of reach, and which went on as walkers.
    CorpsesLost,

    // Resource nodes a gatherer found without charges.
    NodesFoundEmpty,

    // Mob corpses a fighter did not loot because another player's attack owned them.
    LootRefusedOwned,

    // Routes the bots asked the planner for, and the ones it could not queue or find.
    RoutePlans,
    RoutePlansBusy,
    RoutePlansWithoutPath,

    // Walks that ended blocked or too late.
    WalksFailed,

    // Full item states the bots read over HTTP because a committed change reached them only as
    // a newer carry state, as the Unity client does.
    ItemStateReloads,

    // Pauses after a rejected intent.
    RejectionPauses
}

// The playing workload's own numbers: the role mix, the counters of what the bots did, and
// timings that are no request and answer, such as the time from an attack to its first hit.
public sealed class PlayingMetrics(int seed)
{
    public const string AttackFirstHit = "attack_first_hit";
    public const string RoutePlan = "route_plan";
    public const string RouteQueue = "route_queue";

    private readonly object gate = new();
    private readonly long[] counters = new long[Enum.GetValues<PlayingCounter>().Length];
    private readonly Dictionary<string, StressLatencyAccumulator> timings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> roles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> kits = new(StringComparer.Ordinal);
    private volatile bool steady;

    // Route timings count only during the steady interval, where the validity rule judges them.
    public void BeginSteadyMeasurement()
    {
        steady = true;
    }

    public void EndSteadyMeasurement()
    {
        steady = false;
    }

    // Called on a planner thread for every plan.
    public void RecordRouteTiming(Mmorpg.Tools.SimulationBotClient.SimulationBotPlanTiming timing)
    {
        if (!steady)
        {
            return;
        }

        RecordTiming(RouteQueue, timing.QueueWait.TotalMilliseconds);
        RecordTiming(RoutePlan, timing.SearchTime.TotalMilliseconds);
    }

    public void AssignRole(PlayingRole role, string? kit)
    {
        lock (gate)
        {
            var name = PlayingScenario.FormatRole(role);
            roles[name] = roles.GetValueOrDefault(name) + 1;
            if (kit is not null)
            {
                kits[kit] = kits.GetValueOrDefault(kit) + 1;
            }
        }
    }

    public void Count(PlayingCounter counter, long amount = 1)
    {
        Interlocked.Add(ref counters[(int)counter], amount);
    }

    // Planner threads record their timings too, so every timing takes the lock.
    public void RecordTiming(string name, double milliseconds)
    {
        lock (gate)
        {
            if (!timings.TryGetValue(name, out var accumulator))
            {
                accumulator = new StressLatencyAccumulator(unchecked(seed + 900 + timings.Count));
                timings.Add(name, accumulator);
            }

            accumulator.Record(Math.Max(0d, milliseconds));
        }
    }

    public StressLatencySummary? CaptureTiming(string name)
    {
        lock (gate)
        {
            return timings.TryGetValue(name, out var accumulator) ? accumulator.CaptureSummary() : null;
        }
    }

    public PlayingSummary Capture()
    {
        lock (gate)
        {
            return new PlayingSummary(
                new SortedDictionary<string, int>(roles, StringComparer.Ordinal),
                new SortedDictionary<string, int>(kits, StringComparer.Ordinal),
                Enum.GetValues<PlayingCounter>().ToDictionary(
                    counter => char.ToLowerInvariant(counter.ToString()[0]) + counter.ToString()[1..],
                    counter => Interlocked.Read(ref counters[(int)counter]),
                    StringComparer.Ordinal),
                timings
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.CaptureSummary(), StringComparer.Ordinal),
                PlayingExpectedConflicts.Table);
        }
    }
}

public sealed record PlayingSummary(
    IReadOnlyDictionary<string, int> Roles,
    IReadOnlyDictionary<string, int> Kits,
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyDictionary<string, StressLatencySummary?> Timings,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ExpectedConflicts);
