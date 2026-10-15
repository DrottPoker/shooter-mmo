using Mmorpg.GameService.Platform.Configuration;
using StackExchange.Redis;

namespace Mmorpg.GameService.Redis;

public interface IRedisConnectionProvider
{
    ValueTask<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken);
}

public sealed class RedisConnectionProvider : IRedisConnectionProvider, IAsyncDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> connection;

    public RedisConnectionProvider(PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connection = new Lazy<Task<ConnectionMultiplexer>>(
            () => ConnectAsync(options),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async ValueTask<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        var multiplexer = await connection.Value.WaitAsync(cancellationToken);
        return multiplexer.GetDatabase();
    }

    public async ValueTask DisposeAsync()
    {
        if (!connection.IsValueCreated)
        {
            return;
        }

        var multiplexer = await connection.Value;
        await multiplexer.CloseAsync(allowCommandsToComplete: false);
        multiplexer.Dispose();
    }

    private static Task<ConnectionMultiplexer> ConnectAsync(PlatformOptions platformOptions)
    {
        var options = ConfigurationOptions.Parse(platformOptions.RedisConnectionString);
        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        options.ConnectRetry = 1;
        options.ConnectTimeout = checked((int)platformOptions.HealthCheckTimeout.TotalMilliseconds);
        options.AsyncTimeout = checked((int)platformOptions.HealthCheckTimeout.TotalMilliseconds);
        options.ClientName = "shooter-mmo-game-service";
        return ConnectionMultiplexer.ConnectAsync(options);
    }
}
