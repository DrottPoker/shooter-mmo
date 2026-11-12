namespace Mmorpg.SimulationWorker.Realtime;

// What the realtime features register during composition instead of editing the loop, the
// dispatcher, session handling, and the shutdown: their tick phases, message routes,
// lifecycle hooks, and shutdown steps. Filled by RealtimeServerComposition and only read by the
// loop thread afterwards.
internal sealed class RealtimeFeatureRegistries
{
    public SimulationTickPhases TickPhases { get; } = new();

    public RealtimeMessageRoutes Routes { get; } = new();

    public RealtimeLifecycleHooks Hooks { get; } = new();

    public RealtimeShutdownSteps ShutdownSteps { get; } = new();
}
