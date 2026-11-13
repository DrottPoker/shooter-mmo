using Microsoft.Extensions.DependencyInjection;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.WorldData.Characters;

namespace Mmorpg.SimulationWorker.Realtime;

// What looks need beyond the realtime services: the looks of the active characters, the
// appearance content's defaults, and GameService for changes. Without it no player has a look
// and no look changes.
internal sealed record AppearanceDependencies(
    CharacterLookStore Looks,
    AppearanceReplicationSettings Settings,
    GameServiceClient? GameServiceClient)
{
    // Looks without content: an empty look store, the default settings, and no GameService.
    public static AppearanceDependencies CreateDefault()
    {
        return new AppearanceDependencies(new CharacterLookStore(), AppearanceReplicationSettings.Default, null);
    }

    // The settings that the appearance content sets.
    public static AppearanceReplicationSettings SettingsFrom(CharacterAppearanceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new AppearanceReplicationSettings(catalog.DefaultBodyModel, catalog.VisibleEquipmentSlots);
    }

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(provider => new AppearanceDependencies(
            provider.GetRequiredService<CharacterLookStore>(),
            SettingsFrom(provider.GetRequiredService<CharacterAppearanceCatalog>()),
            provider.GetRequiredService<GameServiceClient>()));
    }
}

// Sends every player's look to the peers that see it and carries out look changes.
internal sealed class AppearanceFeature
{
    public AppearanceFeature(RealtimeCoreServices core, AppearanceDependencies dependencies)
    {
        Settings = dependencies.Settings;
        Replicator = new AppearanceReplicator(
            core.Peers,
            core.Sender,
            core.Presence,
            core.EntityRegistry,
            core.ConnectionBindings,
            core.ItemStateStore,
            dependencies.Looks,
            dependencies.Settings);
        var intents = new AppearanceIntentHandler(
            core.Peers,
            core.Sender,
            dependencies.Looks,
            core.OperationTracker,
            core.Completions,
            core.Teardown,
            core.CompletionTargets,
            dependencies.GameServiceClient,
            core.Logger);
        AppearanceIntentHandler.AddRoute(core.Registries.Routes, intents.HandleIntent);
        Replicator.AddTickPhase(core.Registries.TickPhases, core.Config);
    }

    public AppearanceReplicator Replicator { get; }

    public AppearanceReplicationSettings Settings { get; }
}
