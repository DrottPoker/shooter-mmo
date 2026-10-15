using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Redis;
using Npgsql;

namespace Mmorpg.GameService.Platform;

public static class PlatformServiceCollectionExtensions
{
    // One bounded PostgreSQL pool, one Redis multiplexer, the Problem Details pipeline,
    // and the migration runner for the whole process.
    public static IServiceCollection AddGameServicePlatform(
        this IServiceCollection services,
        PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(_ => NpgsqlDataSource.Create(options.PostgresConnectionString));
        services.AddSingleton(options);
        services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddApiProblemDetails();
        services.AddSingleton<DatabaseMigrationRunner>();
        return services;
    }
}
