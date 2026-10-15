using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Mmorpg.GameService;
using Mmorpg.GameService.Config;
using Npgsql;

namespace Mmorpg.Backend.Tests.Integration;

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
}
