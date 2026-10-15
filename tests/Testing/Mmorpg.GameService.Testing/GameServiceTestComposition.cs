using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Mmorpg.GameService;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.Server.Common.Worlds;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Testing;

// Builds the production GameService service graph for tests. The test owns the data
// source, and the module background services are removed so tests control all work.
internal static class GameServiceTestComposition
{
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
                new ItemCatalogSource(Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "WorldData",
                    "Items",
                    "core.item-catalog.json"))),
                ItemOperationsOptions.CreateDefaults(),
                NpcItemLifecycleOptions.Default,
                new StackStressFixtureOptions(false, string.Empty)));
    }
}
