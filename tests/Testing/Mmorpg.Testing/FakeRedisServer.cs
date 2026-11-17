using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mmorpg.Testing;

// A loopback stand-in for Redis that answers only AUTH and PING, for the readiness probes. With
// a password it refuses PING until the connection authenticated, as Redis with requirepass does.
public sealed class FakeRedisServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly string? user;
    private readonly string? password;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task acceptLoop;

    private FakeRedisServer(string? user, string? password)
    {
        this.user = user;
        this.password = password;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        acceptLoop = AcceptAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    // A connection string for this server, with the given password option when one is passed.
    public string ConnectionString(string? passwordOption = null, string? userOption = null)
    {
        var connection = $"127.0.0.1:{Port}";
        if (userOption is not null)
        {
            connection += ",user=" + userOption;
        }

        return passwordOption is null ? connection : connection + ",password=" + passwordOption;
    }

    public static FakeRedisServer Start(string? password = null, string? user = null)
    {
        return new FakeRedisServer(user, password);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        try
        {
            await acceptLoop;
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stopping.Token);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var authenticated = password is null;
                while (await ReadCommandAsync(stream, stopping.Token) is { } command)
                {
                    var reply = command[0].ToUpperInvariant() switch
                    {
                        "AUTH" when Matches(command) => "+OK",
                        "AUTH" => "-WRONGPASS invalid username-password pair",
                        "PING" when authenticated => "+PONG",
                        "PING" => "-NOAUTH Authentication required.",
                        _ => "-ERR unknown command"
                    };
                    authenticated |= reply == "+OK";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\r\n"), stopping.Token);
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }

    private bool Matches(string[] command)
    {
        return command.Length switch
        {
            2 => user is null && command[1] == password,
            3 => command[1] == user && command[2] == password,
            _ => false
        };
    }

    // One RESP array of bulk strings, or null at the end of the connection.
    private static async Task<string[]?> ReadCommandAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadLineAsync(stream, cancellationToken);
        if (header is null || !header.StartsWith('*'))
        {
            return null;
        }

        var parts = new string[int.Parse(header[1..], System.Globalization.CultureInfo.InvariantCulture)];
        for (var index = 0; index < parts.Length; index++)
        {
            _ = await ReadLineAsync(stream, cancellationToken);
            parts[index] = await ReadLineAsync(stream, cancellationToken) ?? string.Empty;
        }

        return parts;
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, cancellationToken) > 0)
        {
            if (buffer[0] == (byte)'\n' && line.Count > 0 && line[^1] == (byte)'\r')
            {
                return Encoding.UTF8.GetString(line.ToArray(), 0, line.Count - 1);
            }

            line.Add(buffer[0]);
        }

        return null;
    }
}
