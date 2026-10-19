using Mmorpg.SimulationWorker.Entities;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.WorldActors;

// Moves Mobs between the dormant and active tiers on their profile's coarse cadence. A
// Mob becomes active when a player comes within the activation range and dormant when
// none is within the larger dormancy range, unless keepActive says it is still busy,
// such as in combat or on its way home. Loop-confined.
public sealed class WorldActorActivityScheduler(WorldActorStore actorStore)
{
    private readonly List<WorldActorRuntimeState> actorBuffer = [];
    private readonly Dictionary<ScheduleKey, List<WorldActorRuntimeState>> schedules = [];
    private readonly List<TierTransition> transitions = [];
    private long observedPopulationRevision = -1;

    public IReadOnlyList<WorldActorRuntimeState> Evaluate(
        IReadOnlyList<PlayerSimulationEntity> players,
        uint serverTick,
        int simulationTickRateHz)
    {
        ArgumentNullException.ThrowIfNull(players);
        return Evaluate(PlayerSpatialGrid.From(players), serverTick, simulationTickRateHz);
    }

    public IReadOnlyList<WorldActorRuntimeState> Evaluate(
        PlayerSpatialGrid players,
        uint serverTick,
        int simulationTickRateHz,
        Func<WorldActorRuntimeState, bool>? keepActive = null)
    {
        ArgumentNullException.ThrowIfNull(players);
        if (simulationTickRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(simulationTickRateHz));
        }

        RefreshSchedules(simulationTickRateHz);
        var changed = new List<WorldActorRuntimeState>();
        transitions.Clear();
        foreach (var schedule in schedules)
        {
            if (serverTick % (uint)schedule.Key.IntervalTicks != 0)
            {
                continue;
            }

            foreach (var actor in schedule.Value)
            {
                if (!actor.IsActive
                    || !actorStore.TryGetActivityProfile(
                        actor.Definition.ActivityProfileId,
                        out var profile))
                {
                    continue;
                }

                var threshold = schedule.Key.ActivityTier
                    == WorldActorActivityTierIds.Active
                    ? profile!.DormancyRange
                    : profile!.ActivationRange;
                var shouldBeActive = players.AnyWithin(actor.PositionX, actor.PositionZ, threshold)
                    || (keepActive is not null && keepActive(actor));
                var targetTier = shouldBeActive
                    ? WorldActorActivityTierIds.Active
                    : WorldActorActivityTierIds.Dormant;
                if (!actor.SetActivityTier(targetTier))
                {
                    continue;
                }

                changed.Add(actor);
                transitions.Add(new TierTransition(
                    schedule.Key,
                    CreateScheduleKey(actor, profile, simulationTickRateHz),
                    actor));
            }
        }

        ApplyTransitions();
        return changed;
    }

    private void RefreshSchedules(int simulationTickRateHz)
    {
        var populationRevision = actorStore.PopulationRevision;
        if (populationRevision == observedPopulationRevision)
        {
            return;
        }

        schedules.Clear();
        actorStore.CopyActorsTo(actorBuffer);
        foreach (var actor in actorBuffer)
        {
            if (!actor.IsActive
                || actor.Definition.Kind != WorldActorKindIds.Mob
                || !actorStore.TryGetActivityProfile(
                    actor.Definition.ActivityProfileId,
                    out var profile))
            {
                continue;
            }

            AddToSchedule(CreateScheduleKey(actor, profile!, simulationTickRateHz), actor);
        }

        observedPopulationRevision = populationRevision;
    }

    private static ScheduleKey CreateScheduleKey(
        WorldActorRuntimeState actor,
        WorldActorActivityProfileDefinition profile,
        int simulationTickRateHz)
    {
        var rate = actor.ActivityTier == WorldActorActivityTierIds.Active
            ? profile.ActiveTickRateHz
            : profile.DormantTickRateHz;
        var intervalTicks = Math.Max(
            1,
            (int)MathF.Round(simulationTickRateHz / rate));
        return new ScheduleKey(profile.Id, actor.ActivityTier, intervalTicks);
    }

    private void ApplyTransitions()
    {
        foreach (var transition in transitions)
        {
            if (schedules.TryGetValue(transition.Previous, out var previous))
            {
                previous.Remove(transition.Actor);
            }

            AddToSchedule(transition.Current, transition.Actor);
        }
    }

    private void AddToSchedule(ScheduleKey key, WorldActorRuntimeState actor)
    {
        if (!schedules.TryGetValue(key, out var actors))
        {
            actors = [];
            schedules.Add(key, actors);
        }

        actors.Add(actor);
    }

    private readonly record struct ScheduleKey(
        string ProfileId,
        string ActivityTier,
        int IntervalTicks);

    private readonly record struct TierTransition(
        ScheduleKey Previous,
        ScheduleKey Current,
        WorldActorRuntimeState Actor);
}
