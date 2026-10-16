using System.Diagnostics;
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

        services.AddSingleton(_ => CreateDataSource(options.PostgresConnectionString));
        services.AddSingleton(options);
        services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddApiProblemDetails();
        services.AddSingleton<DatabaseMigrationRunner>();
        return services;
    }

    // Commands become spans only inside a traced request or operation, so background
    // polling and maintenance never flood the trace store with root spans. Physical
    // connection opens are not traced, because the pool opens them outside any request.
    // Without a tracing listener Npgsql creates no spans at all.
    internal static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        // The name replaces the connection string as the pool label of the Npgsql metrics.
        var builder = new NpgsqlDataSourceBuilder(connectionString) { Name = "postgres" };
        builder.ConfigureTracing(tracing => tracing
            .ConfigureCommandFilter(_ => Activity.Current is not null)
            .ConfigureBatchFilter(_ => Activity.Current is not null)
            .ConfigureCommandSpanNameProvider(command => CommandSpanName(command.CommandText))
            .ConfigureBatchSpanNameProvider(_ => "postgresql batch")
            .EnablePhysicalOpenTracing(false));
        return builder.Build();
    }

    // Names a command span after its SQL verb, such as "postgresql select", so a trace
    // reads without opening every span. The SQL text stays in db.query.text.
    internal static string CommandSpanName(string? commandText)
    {
        var text = commandText.AsSpan().TrimStart();
        var length = 0;
        while (length < text.Length && length < 16 && char.IsAsciiLetter(text[length]))
        {
            length++;
        }

        return length == 0
            ? "postgresql"
            : "postgresql " + text[..length].ToString().ToLowerInvariant();
    }
}
