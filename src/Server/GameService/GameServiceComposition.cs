using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Health;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Platform;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Modules;
using Mmorpg.GameService.Simulation;

namespace Mmorpg.GameService;

// Composes the platform services, the four modules, and the host-owned database and
// health services in one place, so Program.cs and the tests build the same graph.
internal static class GameServiceComposition
{
    // Module SPIs that run inside a caller's transaction. Each needs exactly one
    // implementation, because a missing one would silently skip required work.
    private static readonly Type[] SingleImplementationSpis =
    [
        typeof(IAccountSessionLifecycleHandler),
        typeof(ICharacterLifecycleHandler)
    ];

    // Modules in dependency order. Services register and routes map in this order.
    public static IReadOnlyList<IGameServiceModule> CreateModules(
        GameServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return
        [
            new AccountsModule(configuration.Accounts),
            new CharactersModule(configuration.Characters),
            new PlacementModule(configuration.Placement),
            new ItemsModule(configuration.Items)
        ];
    }

    public static IServiceCollection AddGameService(
        this IServiceCollection services,
        GameServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddGameServicePlatform(configuration.Platform);
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<PostgresHealthProbe>();
        services.AddSingleton<GameServiceHealthService>();
        foreach (var module in CreateModules(configuration))
        {
            module.AddServices(services);
        }

        ValidateSpis(services);
        return services;
    }

    public static void ValidateSpis(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var errors = SingleImplementationSpis
            .Select(spi => (
                Spi: spi,
                Count: services.Count(descriptor => descriptor.ServiceType == spi)))
            .Where(entry => entry.Count != 1)
            .Select(entry =>
                $"- {entry.Spi.FullName} has {entry.Count} implementations, but exactly one is required.")
            .ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException(
                "GameService module composition is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors));
        }
    }

    // UseRouting stays implicit, so WebApplication inserts it first and the endpoint is
    // known to the distributed rate limiter and the sensitive response middleware.
    public static WebApplication UseGameServicePipeline(this WebApplication app)
    {
        app.UseApiPipeline();
        AccountsModule.UseDistributedAuthenticationRateLimiting(app);
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
        foreach (var module in CreateModules(configuration))
        {
            module.MapEndpoints(app);
        }

        return app;
    }
}
