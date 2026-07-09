using System.Net.Sockets;
using ShooterMmo.Shared.Health;

namespace ShooterMmo.Shared.Networking;

public static class TcpHealthProbe
{
    public static async Task<DependencyHealth> CheckAsync(
        string name,
        ServiceEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutToken.CancelAfter(timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeoutToken.Token);

            return new DependencyHealth(name, endpoint.Target, true, null);
        }
        catch (Exception exception) when (exception is SocketException
                                         or OperationCanceledException
                                         or ArgumentException)
        {
            return new DependencyHealth(name, endpoint.Target, false, exception.Message);
        }
    }
}

