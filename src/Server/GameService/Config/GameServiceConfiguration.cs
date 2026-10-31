using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Development;
using Mmorpg.GameService.Items.StackStress;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Placement.Development;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.Server.Telemetry;

namespace Mmorpg.GameService.Config;

// Every validated GameService setting, loaded once before any service is registered.
// Configuration keys are unchanged; each module owns the keys it reads.
internal sealed record GameServiceConfiguration(
    PlatformOptions Platform,
    AccountsModuleOptions Accounts,
    CharactersModuleOptions Characters,
    PlacementModuleOptions Placement,
    ItemsModuleOptions Items)
{
    // Process-level export settings; disabled unless Telemetry:OtlpEndpoint is set.
    public TelemetryOptions Telemetry { get; init; } = TelemetryOptions.Disabled;

    public static GameServiceConfiguration Load(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var core = GameServiceCoreConfiguration.FromConfiguration(configuration);

        // Self-contained option sets report their own errors, in the original order.
        var worlds = WorldManifestCatalog.FromConfiguration(configuration);
        var developmentSimulationBots = DevelopmentSimulationBotOptions.FromConfiguration(
            configuration,
            environment.IsDevelopment());
        var stackStressFixtures = StackStressFixtureOptions.FromConfiguration(
            configuration,
            environment.IsDevelopment(),
            core.Platform.PostgresConnectionString);
        var npcItemLifecycle = NpcItemLifecycleOptions.FromConfiguration(configuration);
        var itemOperations = ItemOperationsOptions.FromConfiguration(configuration);
        var redisAcceleration = RedisAccelerationOptions.FromConfiguration(configuration);
        var itemCatalog = ItemCatalogSource.FromConfiguration(configuration);
        var telemetry = TelemetryOptions.FromConfiguration(configuration);

        return new GameServiceConfiguration(
            core.Platform,
            core.Accounts with { RedisAcceleration = redisAcceleration },
            core.Characters,
            core.Placement with
            {
                Worlds = worlds,
                DevelopmentSimulationBots = developmentSimulationBots,
                DevelopmentLooks = environment.IsDevelopment()
            },
            new ItemsModuleOptions(
                itemCatalog,
                itemOperations,
                npcItemLifecycle,
                stackStressFixtures)
            {
                DevelopmentItemPackages = DevelopmentItemPackageSource.FromConfiguration(configuration)
            })
        {
            Telemetry = telemetry
        };
    }
}

// The required settings of every module, validated together so one startup error
// lists every missing or invalid key.
internal sealed record GameServiceCoreConfiguration(
    PlatformOptions Platform,
    AccountsModuleOptions Accounts,
    CharactersModuleOptions Characters,
    PlacementModuleOptions Placement)
{
    public static GameServiceCoreConfiguration FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<string>();
        var platform = PlatformOptions.Read(configuration, errors);
        var accounts = AccountsModuleOptions.ReadCore(configuration, errors);
        var characters = CharactersModuleOptions.Read(configuration, errors);
        var placement = PlacementModuleOptions.ReadCore(configuration, errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "GameService configuration is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new GameServiceCoreConfiguration(platform, accounts, characters, placement);
    }
}
