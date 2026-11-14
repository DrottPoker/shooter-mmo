using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mmorpg.Tools.StackStressGenerator;

// Reads the key count and the used memory of the disposable Redis with two read-only commands,
// DBSIZE and INFO memory, over its plain protocol, at both ends of the steady interval.
public static class StressRedisSampler
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // A loopback host and port, such as 127.0.0.1:56379.
    public static IPEndPoint ParseEndpoint(string value)
    {
        if (!IPEndPoint.TryParse(value, out var endpoint)
            || endpoint.Port == 0
            || !IPAddress.IsLoopback(endpoint.Address))
        {
            throw new StressGeneratorOptionException(
                "--redis-endpoint must be a loopback address with a port, such as 127.0.0.1:56379.");
        }

        return endpoint;
    }

    public static async Task<StressRedisSnapshot> ReadAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("*1\r\n$6\r\nDBSIZE\r\n*2\r\n$4\r\nINFO\r\n$6\r\nmemory\r\n"), timeout.Token);
        var reader = new RespReader(stream);
        var keys = ParseInteger(await reader.ReadLineAsync(timeout.Token));
        var info = await reader.ReadBulkAsync(timeout.Token);
        return new StressRedisSnapshot(DateTime.UtcNow, keys, ParseUsedMemory(info));
    }

    public static long ParseUsedMemory(string info)
    {
        ArgumentNullException.ThrowIfNull(info);
        foreach (var line in info.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("used_memory:", StringComparison.Ordinal)
                && long.TryParse(trimmed["used_memory:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
            {
                return bytes;
            }
        }

        throw new InvalidDataException("Redis INFO memory has no used_memory line.");
    }

    private static long ParseInteger(string line)
    {
        return line.Length > 1 && line[0] == ':' && long.TryParse(line[1..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Redis answered DBSIZE with '{line}'.");
    }

    private sealed class RespReader(Stream stream)
    {
        private readonly byte[] buffer = new byte[64 * 1024];
        private int start;
        private int end;

        public async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                for (var index = start; index + 1 < end; index++)
                {
                    if (buffer[index] == '\r' && buffer[index + 1] == '\n')
                    {
                        var line = Encoding.ASCII.GetString(buffer, start, index - start);
                        start = index + 2;
                        return line;
                    }
                }

                await FillAsync(cancellationToken);
            }
        }

        public async Task<string> ReadBulkAsync(CancellationToken cancellationToken)
        {
            var header = await ReadLineAsync(cancellationToken);
            if (header.Length < 2 || header[0] != '$' || !int.TryParse(header[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                throw new InvalidDataException($"Redis answered INFO with '{header}'.");
            }

            while (end - start < length + 2)
            {
                await FillAsync(cancellationToken);
            }

            var text = Encoding.UTF8.GetString(buffer, start, length);
            start += length + 2;
            return text;
        }

        private async Task FillAsync(CancellationToken cancellationToken)
        {
            if (start > 0)
            {
                Array.Copy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }

            if (end == buffer.Length)
            {
                throw new InvalidDataException("A Redis answer is larger than expected.");
            }

            var read = await stream.ReadAsync(buffer.AsMemory(end), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Redis closed the connection.");
            }

            end += read;
        }
    }
}
