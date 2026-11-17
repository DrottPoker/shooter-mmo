using System.Net.Sockets;
using System.Text;
using Mmorpg.Server.Common.Networking;

namespace Mmorpg.Server.Common.Health;

// A readiness probe of its own connection: AUTH when the connection string has a password,
// then PING, within the timeout.
public static class RedisHealthProbe
{
    private const int MaximumReplyLength = 512;
    private static readonly byte[] PingCommand = Encoding.ASCII.GetBytes("*1\r\n$4\r\nPING\r\n");

    public static async Task<DependencyHealth> CheckAsync(
        string connectionString,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var endpoint = RedisConnectionString.ParseRequiredEndpoint(connectionString);
        var credentials = RedisConnectionString.ParseCredentials(connectionString);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeoutSource.Token);
            await using var stream = client.GetStream();
            if (credentials.Password is { } password)
            {
                await stream.WriteAsync(AuthCommand(credentials.User, password), timeoutSource.Token);
                var authReply = await ReadReplyAsync(stream, timeoutSource.Token);
                if (authReply != "+OK")
                {
                    return new DependencyHealth(
                        "redis",
                        endpoint.Target,
                        false,
                        "Redis refused the credentials of the connection string.");
                }
            }

            await stream.WriteAsync(PingCommand, timeoutSource.Token);
            var succeeded = await ReadReplyAsync(stream, timeoutSource.Token) == "+PONG";

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

    private static byte[] AuthCommand(string? user, string password)
    {
        string[] arguments = user is null ? ["AUTH", password] : ["AUTH", user, password];
        var command = new StringBuilder();
        command.Append('*').Append(arguments.Length).Append("\r\n");
        foreach (var argument in arguments)
        {
            command.Append('$').Append(Encoding.UTF8.GetByteCount(argument)).Append("\r\n")
                .Append(argument).Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(command.ToString());
    }

    // One reply line without its CRLF, or null when the connection ended or the line is too long.
    private static async Task<string?> ReadReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        var buffer = new byte[1];
        while (line.Count < MaximumReplyLength)
        {
            if (await stream.ReadAsync(buffer, cancellationToken) == 0)
            {
                return null;
            }

            if (buffer[0] == (byte)'\n' && line.Count > 0 && line[^1] == (byte)'\r')
            {
                return Encoding.UTF8.GetString(line.ToArray(), 0, line.Count - 1);
            }

            line.Add(buffer[0]);
        }

        return null;
    }
}
