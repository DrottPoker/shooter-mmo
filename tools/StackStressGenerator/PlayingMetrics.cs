namespace Mmorpg.Tools.StackStressGenerator;

public enum PlayingCounter
{
    // Mobs a bot killed, by the Died event that names it the killer.
    Kills,

    // Damage that Mobs dealt the bots.
    MobDamageTaken,

    // Deaths of the bots, and of the bots of every role.
    BotDeaths,
    FighterDeaths,
    GathererDeaths,
    WalkerDeaths,
    TraderDeaths,

    // Bots that walked back to their own corpse and looted it back.
    CorpsesRecovered,

    // Bots whose corpse was gone or out of reach, and which went on as walkers.
    CorpsesLost,

    // Nodes a gatherer chose and walked to, the ones it found depleted when it arrived or
    // opened them, and the times it found no node with charges in its whole field.
    GatherAttempts,
    NodesFoundEmpty,
    NodeFieldsEmpty,

    // Yields that reached a gatherer, and how its gathering stopped.
    GatherYields,
    GatherStopsDepleted,
    GatherStopsOther,

    // Nodes a gatherer gave up because cover blocked its line of sight to them.
    NodesBlockedFromSight,

    // Trips to the vendor, the stacks the bots sold and bought there, and the currency that
    // changed hands. A trip that left the bot without room counts once more.
    VendorTrips,
    VendorSales,
    VendorPurchases,
    CurrencyEarned,
    CurrencySpent,
    VendorTripsWithoutRoom,

    // Trips that found no vendor, or no place in reach of it.
    VendorTripsFailed,

    // Arrows bow fighters bought at the vendor, and bow fighters that had neither arrows nor
    // the currency for more and went on as walkers.
    ArrowsBought,
    ArrowsRanOut,

    // Times a fighter fled from a fight, and Mobs it passed over because other aggressive
    // Mobs stood within reach of them.
    FighterFlees,
    CrowdedMobsSkipped,

    // Times a gatherer or trader fled from a Mob that attacked it.
    NonFighterFlees,

    // Item moves of the traders between their own slots.
    TraderItemMoves,

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
    RejectionPauses,

    // The churn: leaves the schedule asked for, the ones a bot skipped because it was still
    // away from an earlier one, could not leave safely in time, or the steady interval ended,
    // and the walks away from aggressive Mobs before a leave.
    ChurnRequests,
    ChurnSkippedBusy,
    ChurnSkippedUnsafe,
    ChurnSkippedLate,
    ChurnRetreats,

    // Leaves of the churn, by role and with an account logout.
    Leaves,
    LeavesFighter,
    LeavesGatherer,
    LeavesWalker,
    LeavesTrader,
    LeavesWithLogout,

    // Returns that took the body over and that entered from the saved place, the returns meant
    // for the saved place that still found the body, because hits kept it, and the returns meant
    // for the body that found it gone, because it died.
    RejoinsBody,
    RejoinsSaved,
    RejoinsSavedFoundBody,
    RejoinsBodyFoundGone,

    // Bodies that a Mob hit or killed while they stood, as the saved-place returns show it.
    RejoinBodiesHit,
    RejoinBodiesDied,

    // Returns whose place or health check failed.
    RejoinChecksFailed
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
    private readonly List<PlayingRejoinMismatch> mismatches = [];
    private long[]? steadyStartCounters;
    private long[]? steadyEndCounters;
    private long steadyStartTimestamp;
    private long steadyEndTimestamp;
    private volatile bool steady;

    // The first mismatches of the churn's rejoin check keep their evidence; the count goes on.
    public const int MaximumKeptMismatches = 20;

    // Route timings count only during the steady interval, where the validity rule judges them,
    // and the counters of the steady interval are the difference of its ends.
    public void BeginSteadyMeasurement()
    {
        lock (gate)
        {
            steadyStartCounters = ReadCounters();
            steadyStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        steady = true;
    }

    public void EndSteadyMeasurement()
    {
        steady = false;
        lock (gate)
        {
            steadyEndCounters = ReadCounters();
            steadyEndTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    public void RecordRejoinMismatch(PlayingRejoinMismatch mismatch)
    {
        Count(PlayingCounter.RejoinChecksFailed);
        lock (gate)
        {
            if (mismatches.Count < MaximumKeptMismatches)
            {
                mismatches.Add(mismatch);
            }
        }
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
            var steadyCounters = steadyStartCounters is { } start && steadyEndCounters is { } end
                ? Enum.GetValues<PlayingCounter>().ToDictionary(
                    Name,
                    counter => end[(int)counter] - start[(int)counter],
                    StringComparer.Ordinal)
                : null;
            var steadySeconds = steadyStartCounters is not null && steadyEndCounters is not null
                ? (steadyEndTimestamp - steadyStartTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency
                : 0d;
            return new PlayingSummary(
                new SortedDictionary<string, int>(roles, StringComparer.Ordinal),
                new SortedDictionary<string, int>(kits, StringComparer.Ordinal),
                Enum.GetValues<PlayingCounter>().ToDictionary(
                    Name,
                    counter => Interlocked.Read(ref counters[(int)counter]),
                    StringComparer.Ordinal),
                timings
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.CaptureSummary(), StringComparer.Ordinal),
                PlayingExpectedConflicts.Table)
            {
                SteadySeconds = steadySeconds,
                SteadyCounters = steadyCounters,
                DeathsPerBotPerTenMinutes = steadyCounters is null
                    ? null
                    : DeathsPerBotPerTenMinutes(roles, steadyCounters, steadySeconds),
                RejoinMismatches = mismatches.ToArray()
            };
        }
    }

    // The deaths of every role in the steady interval, per bot of the role and ten minutes, the
    // number the scenarios are tuned to.
    public static IReadOnlyDictionary<string, double> DeathsPerBotPerTenMinutes(
        IReadOnlyDictionary<string, int> roleCounts,
        IReadOnlyDictionary<string, long> steadyCounters,
        double steadySeconds)
    {
        var result = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (steadySeconds <= 0d)
        {
            return result;
        }

        foreach (var role in Enum.GetValues<PlayingRole>())
        {
            var name = PlayingScenario.FormatRole(role);
            var bots = roleCounts.GetValueOrDefault(name);
            if (bots <= 0)
            {
                continue;
            }

            var deaths = steadyCounters.GetValueOrDefault(Name(role switch
            {
                PlayingRole.Fighter => PlayingCounter.FighterDeaths,
                PlayingRole.Gatherer => PlayingCounter.GathererDeaths,
                PlayingRole.Trader => PlayingCounter.TraderDeaths,
                _ => PlayingCounter.WalkerDeaths
            }));
            result[name] = deaths / (double)bots / (steadySeconds / 600d);
        }

        return result;
    }

    public static string Name(PlayingCounter counter)
    {
        var text = counter.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    private long[] ReadCounters()
    {
        var values = new long[counters.Length];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = Interlocked.Read(ref counters[index]);
        }

        return values;
    }
}

public sealed record PlayingSummary(
    IReadOnlyDictionary<string, int> Roles,
    IReadOnlyDictionary<string, int> Kits,
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyDictionary<string, StressLatencySummary?> Timings,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ExpectedConflicts)
{
    // The steady interval's length and its counters, the difference of its ends.
    public double SteadySeconds { get; init; }

    public IReadOnlyDictionary<string, long>? SteadyCounters { get; init; }

    // The deaths of every role per bot of the role and ten minutes of the steady interval.
    public IReadOnlyDictionary<string, double>? DeathsPerBotPerTenMinutes { get; init; }

    // The first rejoins of the churn whose check failed, with their evidence.
    public IReadOnlyList<PlayingRejoinMismatch> RejoinMismatches { get; init; } = [];
}
