using Mmorpg.Server.Common.Health;
using Mmorpg.Server.Common.Networking;
using Npgsql;

namespace Mmorpg.GameService.Health;

public sealed class PostgresHealthProbe(
    NpgsqlDataSource dataSource,
    Mmorpg.GameService.Config.GameServiceConfig config)
{
    public async Task<DependencyHealth> CheckAsync(CancellationToken cancellationToken)
    {
        var endpoint = PostgresConnectionString.ParseEndpoint(config.PostgresConnectionString);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(config.HealthCheckTimeout);

        try
        {
            await using var command = dataSource.CreateCommand("select 1;");
            var result = await command.ExecuteScalarAsync(timeoutSource.Token);
            var succeeded = Convert.ToInt32(result) == 1;

            return new DependencyHealth(
                "postgres",
                endpoint.Target,
                succeeded,
                succeeded ? null : "PostgreSQL returned an unexpected query result.");
        }
        catch (Exception exception) when (exception is NpgsqlException
                                         or IOException
                                         or OperationCanceledException)
        {
            return new DependencyHealth("postgres", endpoint.Target, false, exception.Message);
        }
    }
}
