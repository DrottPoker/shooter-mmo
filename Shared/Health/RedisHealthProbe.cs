using System.Net.Sockets;
using System.Text;
using ShooterMmo.Shared.Networking;

namespace ShooterMmo.Shared.Health;

public static class RedisHealthProbe
{
    private static readonly byte[] PingCommand = Encoding.ASCII.GetBytes("*1\r\n$4\r\nPING\r\n");
    private static readonly byte[] ExpectedResponse = Encoding.ASCII.GetBytes("+PONG\r\n");

    public static async Task<DependencyHealth> CheckAsync(
        string connectionString,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var endpoint = RedisConnectionString.ParseRequiredEndpoint(connectionString);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeoutSource.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(PingCommand, timeoutSource.Token);

            var response = new byte[ExpectedResponse.Length];
            var bytesRead = await stream.ReadAtLeastAsync(
                response,
                ExpectedResponse.Length,
                throwOnEndOfStream: false,
                timeoutSource.Token);

            var succeeded = bytesRead == ExpectedResponse.Length
                && response.AsSpan().SequenceEqual(ExpectedResponse);

            return new DependencyHealth(
                "redis",
                endpoint.Target,
                succeeded,
                succeeded ? null : "Redis did not return PONG.");
        }
        catch (Exception exception) when (exception is SocketException
                                         or IOException
                                         or OperationCanceledException)
        {
            return new DependencyHealth("redis", endpoint.Target, false, exception.Message);
        }
    }
}
