namespace Mmorpg.SimulationWorker.Realtime;

// The level and experience of the characters in the world. The replicator exists before the
// features that commit changes, the combat authority, the world interactions, the quest intents,
// and the admin tools, which apply what GameService committed through it. Its hooks come after the
// admin feature's, so a joining owner hears its progression after its admin state.
internal sealed class ProgressionFeature
{
    public ProgressionFeature(RealtimeCoreServices core)
    {
        Replicator = new ProgressionReplicator(
            core.Peers,
            core.Sender,
            core.Presence,
            core.EntityRegistry,
            core.ConnectionBindings);
    }

    public ProgressionReplicator Replicator { get; }

    public void AddHooks(RealtimeFeatureRegistries registries)
    {
        ArgumentNullException.ThrowIfNull(registries);
        var replicator = Replicator;
        registries.Hooks.AddPlayerJoined(replicator.OnPlayerJoined);
        registries.Hooks.AddPlayerRemoved(replicator.OnPlayerRemoved);
        registries.ShutdownSteps.Add(RealtimeShutdownOrder.Progression, "progression", replicator.Clear);
    }
}
