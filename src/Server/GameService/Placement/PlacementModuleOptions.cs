using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Configuration;

namespace Mmorpg.GameService.Placement;

public sealed record PlacementModuleOptions(
    int JoinTicketLifetimeSeconds,
    int SessionLeaseLifetimeSeconds,
    TimeSpan WorkerHeartbeatTimeout,
    SimulationTopologyConfig Topology)
{
    // Read by their own factories after the core settings are valid.
    public WorldManifestCatalog Worlds { get; init; } = WorldManifestCatalog.FromManifests([]);

    public DevelopmentSimulationBotOptions DevelopmentSimulationBots { get; init; } =
        DevelopmentSimulationBotOptions.Disabled;

    public SimulationWorkerLeasePolicy WorkerLeasePolicy => new(WorkerHeartbeatTimeout);

    public static PlacementModuleOptions ReadCore(
        IConfiguration configuration,
        ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(errors);

        var joinTicketLifetimeSeconds = ConfigurationValidation.ValidatePositiveInt(
            configuration,
            "Simulation:JoinTicketLifetimeSeconds",
            errors);
        var sessionLeaseLifetimeSeconds = ConfigurationValidation.ValidatePositiveInt(
            configuration,
            "Simulation:SessionLeaseLifetimeSeconds",
            errors);
        var workerHeartbeatTimeoutSeconds = ConfigurationValidation.ValidatePositiveInt(
            configuration,
            "Simulation:WorkerHeartbeatTimeoutSeconds",
            errors);
        var topology = SimulationTopologyConfig.FromConfiguration(configuration, errors);

        return new PlacementModuleOptions(
            joinTicketLifetimeSeconds,
            sessionLeaseLifetimeSeconds,
            TimeSpan.FromSeconds(workerHeartbeatTimeoutSeconds),
            topology);
    }
}
