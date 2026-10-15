using Mmorpg.GameService.Items;
using Mmorpg.GameService.Redis;
using Mmorpg.GameService.Simulation;

namespace Mmorpg.GameService.Config;

// Every validated GameService setting, loaded once before any service is registered.
internal sealed record GameServiceConfiguration(
    GameServiceConfig Core,
    WorldManifestCatalog Worlds,
    DevelopmentSimulationBotOptions DevelopmentSimulationBots,
    StackStressFixtureOptions StackStressFixtures,
    NpcItemLifecycleOptions NpcItemLifecycle,
    ItemOperationsOptions ItemOperations,
    RedisAccelerationOptions RedisAcceleration,
    ItemCatalogSource ItemCatalog)
{
    public static GameServiceConfiguration Load(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var core = GameServiceConfig.FromConfiguration(configuration);
        var worlds = WorldManifestCatalog.FromConfiguration(configuration);
        var developmentSimulationBots = DevelopmentSimulationBotOptions.FromConfiguration(
            configuration,
            environment.IsDevelopment());
        var stackStressFixtures = StackStressFixtureOptions.FromConfiguration(
            configuration,
            environment.IsDevelopment(),
            core.PostgresConnectionString);
        var npcItemLifecycle = NpcItemLifecycleOptions.FromConfiguration(configuration);
        var itemOperations = ItemOperationsOptions.FromConfiguration(configuration);
        var redisAcceleration = RedisAccelerationOptions.FromConfiguration(configuration);
        var itemCatalog = ItemCatalogSource.FromConfiguration(configuration);

        return new GameServiceConfiguration(
            core,
            worlds,
            developmentSimulationBots,
            stackStressFixtures,
            npcItemLifecycle,
            itemOperations,
            redisAcceleration,
            itemCatalog);
    }
}
