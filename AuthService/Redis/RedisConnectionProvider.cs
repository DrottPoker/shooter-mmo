using StackExchange.Redis;

namespace AuthService.Redis;

public interface IRedisConnectionProvider
{
    ValueTask<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken);
}

public sealed class RedisConnectionProvider : IRedisConnectionProvider, IAsyncDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> connection;

    public RedisConnectionProvider(AuthService.Config.AuthServiceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        connection = new Lazy<Task<ConnectionMultiplexer>>(
            () => ConnectAsync(config),
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

    private static Task<ConnectionMultiplexer> ConnectAsync(
        AuthService.Config.AuthServiceConfig config)
    {
        var options = ConfigurationOptions.Parse(config.RedisConnectionString);
        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        options.ConnectRetry = 1;
        options.ConnectTimeout = checked((int)config.HealthCheckTimeout.TotalMilliseconds);
        options.AsyncTimeout = checked((int)config.HealthCheckTimeout.TotalMilliseconds);
        options.ClientName = "shooter-mmo-auth-service";
        return ConnectionMultiplexer.ConnectAsync(options);
    }
}
