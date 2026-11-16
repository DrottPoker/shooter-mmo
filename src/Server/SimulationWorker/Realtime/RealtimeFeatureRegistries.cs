namespace Mmorpg.SimulationWorker.Realtime;

// What the realtime features register during composition instead of editing the loop, the
// dispatcher, session handling, and the shutdown: their tick phases, the lanes of their periodic
// phases, message routes, lifecycle hooks, and shutdown steps. Filled by the feature units that
// RealtimeServerComposition creates, and only read by the loop thread afterwards.
internal sealed class RealtimeFeatureRegistries(SimulationTickCadence tickCadence)
{
    public SimulationTickPhases TickPhases { get; } = new();

    // On which ticks the phases that do not run every tick do their work.
    public SimulationTickCadence TickCadence { get; } = tickCadence;

    public RealtimeMessageRoutes Routes { get; } = new();

    public RealtimeLifecycleHooks Hooks { get; } = new();

    public RealtimeShutdownSteps ShutdownSteps { get; } = new();
}
