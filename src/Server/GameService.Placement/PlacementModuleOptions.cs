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

    // How often the shard gauges re-read the shard list. Optional, 15 seconds by default.
    public TimeSpan MetricsInterval { get; init; } = TimeSpan.FromSeconds(15);

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
        var metricsIntervalSeconds = configuration["Simulation:MetricsIntervalSeconds"] is null
            ? 15
            : ConfigurationValidation.ValidateIntRange(
                configuration,
                "Simulation:MetricsIntervalSeconds",
                1,
                3600,
                errors);

        return new PlacementModuleOptions(
            joinTicketLifetimeSeconds,
            sessionLeaseLifetimeSeconds,
            TimeSpan.FromSeconds(workerHeartbeatTimeoutSeconds),
            topology)
        {
            MetricsInterval = TimeSpan.FromSeconds(metricsIntervalSeconds)
        };
    }
}
