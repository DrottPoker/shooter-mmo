using ShooterMmo.WorldData.Actors;
using SimulationWorker.Entities;

namespace SimulationWorker.WorldActors;

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
                var thresholdSquared = threshold * threshold;
                var shouldBeActive = players.Any(player =>
                {
                    var state = player.Movement.State;
                    var deltaX = state.PositionX - actor.PositionX;
                    var deltaZ = state.PositionZ - actor.PositionZ;
                    return (deltaX * deltaX) + (deltaZ * deltaZ) <= thresholdSquared;
                });
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
