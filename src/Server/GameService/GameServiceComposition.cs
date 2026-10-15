using Mmorpg.GameService.Auth;
using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Health;
using Mmorpg.GameService.Http;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Redis;
using Mmorpg.GameService.Simulation;
using Npgsql;

namespace Mmorpg.GameService;

// Composes the platform services, the four modules, and the host-owned database and
// health services in one place, so Program.cs and the tests build the same graph.
internal static class GameServiceComposition
{
    public static IServiceCollection AddGameService(
        this IServiceCollection services,
        GameServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var config = configuration.Core;

        services.AddSingleton(_ => NpgsqlDataSource.Create(config.PostgresConnectionString));
        services.AddSingleton(config);
        services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddApiProblemDetails();

        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<PostgresHealthProbe>();
        services.AddSingleton<GameServiceHealthService>();

        services.AddAccountsModule(config, configuration.RedisAcceleration);
        services.AddCharactersModule();
        services.AddPlacementModule(
            configuration.Worlds,
            configuration.DevelopmentSimulationBots);
        services.AddItemsModule(
            configuration.ItemCatalog,
            configuration.StackStressFixtures,
            configuration.NpcItemLifecycle,
            configuration.ItemOperations);
        return services;
    }

    // UseRouting stays implicit, so WebApplication inserts it first and the endpoint is
    // known to the distributed rate limiter and the sensitive response middleware.
    public static WebApplication UseGameServicePipeline(this WebApplication app)
    {
        app.UseApiPipeline();
        app.UseDistributedAuthenticationRateLimiting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    public static IEndpointRouteBuilder MapGameService(
        this IEndpointRouteBuilder app,
        GameServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        app.MapGameServiceHealthEndpoints();
        app.MapAccountsModule();
        app.MapCharactersModule();
        app.MapPlacementModule(configuration.DevelopmentSimulationBots);
        app.MapItemsModule(configuration.StackStressFixtures);
        return app;
    }
}
