using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Mmorpg.GameService;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Development;
using Mmorpg.GameService.Items.StackStress;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.Server.Common.Worlds;
using Mmorpg.Testing;
using Mmorpg.WorldData.Content;
using Npgsql;

namespace Mmorpg.GameService.Testing;

// Builds the production GameService service graph for tests. The test owns the data
// source, and the module background services are removed so tests control all work.
internal static class GameServiceTestComposition
{
    // The default test configuration. Module test projects use this overload, because a
    // kit member typed with a host configuration type is inaccessible to them.
    public static IServiceCollection AddGameServiceForTests(
        this IServiceCollection services,
        NpgsqlDataSource dataSource)
    {
        return services.AddGameServiceForTests(
            dataSource,
            CreateConfiguration(dataSource.ConnectionString));
    }

    public static IServiceCollection AddGameServiceForTests(
        this IServiceCollection services,
        NpgsqlDataSource dataSource,
        GameServiceConfiguration configuration)
    {
        services.AddLogging();
        services.AddGameService(configuration);
        services.Replace(ServiceDescriptor.Singleton(dataSource));

        var moduleHostedServices = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType?.Assembly.GetName().Name?.StartsWith(
                    "Mmorpg.GameService",
                    StringComparison.Ordinal) == true)
            .ToArray();
        foreach (var descriptor in moduleHostedServices)
        {
            services.Remove(descriptor);
        }

        return services;
    }

    // A standalone service provider for tests that need services without a web host.
    // Module test projects use this overload, so they never name host configuration types.
    public static ServiceProvider BuildServiceProvider(NpgsqlDataSource dataSource)
    {
        return BuildServiceProvider(dataSource, CreateConfiguration(dataSource.ConnectionString));
    }

    public static ServiceProvider BuildServiceProvider(
        NpgsqlDataSource dataSource,
        GameServiceConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddGameServiceForTests(dataSource, configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    // The NPC lifecycle grants the fixture catalog's quest item.
    public static NpcItemLifecycleOptions FixtureNpcLifecycle { get; } =
        new(100, "quest.fixture.letter", "fixture.letter", 1);

    // The settings every integration test runs with. Redis acceleration stays disabled,
    // so tests never reach Redis.
    public static GameServiceConfiguration CreateConfiguration(string postgresConnectionString)
    {
        var topology = new SimulationTopologyConfig(
            [new FleetBootstrapConfig("local-fleet", "Local Development", "LOCAL")],
            [new SimulationNodeBootstrapConfig("local-node-1", "local-fleet", "Local Node 1")],
            [new ShardBootstrapConfig(
                "local-shard-1",
                "local-fleet",
                "Local Shard 1",
                "mvp-open-risk")]);
        return new GameServiceConfiguration(
            new PlatformOptions(
                postgresConnectionString,
                "localhost:6379",
                TimeSpan.FromSeconds(1),
                RunMigrationsOnStartup: true),
            new AccountsModuleOptions(
                TimeSpan.FromHours(24),
                5,
                TimeSpan.FromSeconds(60),
                new SimulationWorkerCredentials([], null)),
            new CharactersModuleOptions(5),
            new PlacementModuleOptions(30, 60, TimeSpan.FromSeconds(30), topology)
            {
                Worlds = WorldManifestCatalog.FromManifests(
                    WorldManifestFileStore.LoadAll(RepositoryPaths.Worlds))
            },
            new ItemsModuleOptions(
                // Tests run against the test-owned item catalog, not the game content.
                new ItemCatalogSource(RepositoryPaths.ItemCatalogFixtureRuntimePath),
                ItemOperationsOptions.CreateDefaults(),
                FixtureNpcLifecycle,
                new StackStressFixtureOptions(false, string.Empty))
            {
                DevelopmentItemPackages = new DevelopmentItemPackageSource(
                    ContentLayout.DevelopmentItemPackagesPath(RepositoryPaths.ItemCatalogFixtureContent))
            });
    }
}
