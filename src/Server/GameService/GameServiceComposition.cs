using Mmorpg.GameService.Auth;
using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Health;
using Mmorpg.GameService.Http;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Platform.Configuration;
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
        var platform = configuration.Platform;

        services.AddSingleton(_ => NpgsqlDataSource.Create(platform.PostgresConnectionString));
        services.AddSingleton(platform);
        services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddApiProblemDetails();

        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<PostgresHealthProbe>();
        services.AddSingleton<GameServiceHealthService>();

        services.AddAccountsModule(configuration.Accounts);
        services.AddCharactersModule(configuration.Characters);
        services.AddPlacementModule(configuration.Placement);
        services.AddItemsModule(configuration.Items);
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
        app.MapPlacementModule(configuration.Placement);
        app.MapItemsModule(configuration.Items);
        return app;
    }
}
