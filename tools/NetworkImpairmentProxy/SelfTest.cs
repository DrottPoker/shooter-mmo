using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Mmorpg.Tools.NetworkImpairmentProxy;

public sealed record SelfTestOptions(
    double DelayMs,
    int Seconds,
    int Clients,
    int PacketsPerSecondPerClient,
    int PacketBytes,
    string? OutputPath)
{
    public static readonly SelfTestOptions Default = new(40, 20, 8, 100, 120, null);

    // How long before a due time the proxy's threads spin, as in a real run by default.
    public TimeSpan Spin { get; init; } = PacketScheduler.DefaultSpin;

    // The p99 of the error that the proxy's delay may have, in either direction.
    public const double MaximumP99ErrorMs = 2;
}

public sealed record SelfTestDirectionResult(
    long Sent,
    long Received,
    ProxyMillisecondSummary? ErrorMs);

public sealed record SelfTestResult(
    double DelayMs,
    int Seconds,
    int Clients,
    int PacketsPerSecondPerClient,
    int PacketBytes,
    bool HighResolutionTimer,
    SelfTestDirectionResult ClientToServer,
    SelfTestDirectionResult ServerToClient,
    ProxyCountsReport Proxy,
    bool Passed);

// Proves the proxy's delay: it runs the proxy in this process with a constant delay and no
// loss in front of an echo server, sends timestamped packets from several client sockets
// through it, and measures with one clock how much later than the delay each packet arrived,
// at the server and back at the client. Receivers run on their own threads, so the measurement
// itself adds as little as possible.
public static class SelfTest
{
    private const uint Magic = 0x4E495054;
    private const int HeaderBytes = 4 + 2 + 4 + 8 + 8;

    public static SelfTestResult Run(SelfTestOptions options, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        var profile = new NetworkProfile(
            "self-test",
            "A constant delay without jitter or loss.",
            new DirectionProfile(options.DelayMs, 0, 0, 0, null, null),
            new DirectionProfile(options.DelayMs, 0, 0, 0, null, null),
            new NetworkProfileVerdict([], []));
        var delayTicks = ProxyTime.Ticks(options.DelayMs);
        var toServerErrors = new MillisecondHistogram(0.01, 1_000);
        var toClientErrors = new MillisecondHistogram(0.01, 1_000);
        var gate = new object();
        long toServerReceived = 0;
        long toClientReceived = 0;
        long sent = 0;
        using var running = new CancellationTokenSource();

        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var proxy = new ImpairmentProxy(
            new ImpairmentProxyOptions(
                new IPEndPoint(IPAddress.Loopback, 0),
                (IPEndPoint)server.LocalEndPoint!,
                1,
                ImpairmentProxyOptions.DefaultIdleTimeout,
                options.Clients + 1,
                false)
            {
                Spin = options.Spin
            },
            profile);
        proxy.Start();
        var proxyEndPoint = proxy.ListenEndPoint;

        var serverThread = StartReceiver("Self-test server", () =>
        {
            var buffer = new byte[options.PacketBytes + 64];
            var from = new SocketAddress(AddressFamily.InterNetwork);
            while (!running.IsCancellationRequested)
            {
                int length;
                try
                {
                    length = server.ReceiveFrom(buffer, SocketFlags.None, from);
                }
                catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
                {
                    return;
                }

                var now = Stopwatch.GetTimestamp();
                if (length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(buffer) != Magic)
                {
                    continue;
                }

                var sentAt = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(10));
                lock (gate)
                {
                    toServerErrors.Record(ProxyTime.Milliseconds(now - sentAt - delayTicks));
                    toServerReceived++;
                }

                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(18), Stopwatch.GetTimestamp());
                try
                {
                    server.SendTo(buffer.AsSpan(0, length), SocketFlags.None, from);
                }
                catch (SocketException)
                {
                    // The client counts what did not come back.
                }
            }
        });

        var clients = new Socket[options.Clients];
        var clientThreads = new Thread[options.Clients];
        for (var index = 0; index < clients.Length; index++)
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            client.Connect(proxyEndPoint);
            clients[index] = client;
            clientThreads[index] = StartReceiver($"Self-test client {index + 1}", () =>
            {
                var buffer = new byte[options.PacketBytes + 64];
                while (!running.IsCancellationRequested)
                {
                    int length;
                    try
                    {
                        length = client.Receive(buffer, SocketFlags.None);
                    }
                    catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
                    {
                        return;
                    }

                    var now = Stopwatch.GetTimestamp();
                    if (length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(buffer) != Magic)
                    {
                        continue;
                    }

                    var echoedAt = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(18));
                    lock (gate)
                    {
                        toClientErrors.Record(ProxyTime.Milliseconds(now - echoedAt - delayTicks));
                        toClientReceived++;
                    }
                }
            });
        }

        output.WriteLine(FormattableString.Invariant(
            $"Self-test: {options.Clients} clients send {options.PacketsPerSecondPerClient} packets of {options.PacketBytes} bytes per second each for {options.Seconds} s through a proxy with {options.DelayMs:0.###} ms of delay each way and no jitter or loss; the proxy spins the last {options.Spin.TotalMilliseconds:0.###} ms before a due time."));

        // One sender paces every client evenly with the same precise waiter as the proxy.
        var packet = new byte[options.PacketBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic);
        var interval = TimeSpan.FromSeconds(1d / (options.PacketsPerSecondPerClient * (double)options.Clients));
        var intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        var end = Stopwatch.GetTimestamp() + (options.Seconds * Stopwatch.Frequency);
        var next = Stopwatch.GetTimestamp();
        var sequence = 0u;
        bool highResolution;
        using (var waiter = new PreciseWaiter())
        using (var never = new ManualResetEvent(false))
        {
            highResolution = waiter.IsHighResolution;
            while (next < end)
            {
                var now = Stopwatch.GetTimestamp();
                if (now < next)
                {
                    waiter.Wait(TimeSpan.FromTicks((long)((next - now) * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)), never);
                    continue;
                }

                var client = (int)(sequence % (uint)clients.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), (ushort)client);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), sequence++);
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(10), Stopwatch.GetTimestamp());
                clients[client].Send(packet, SocketFlags.None);
                sent++;
                next += intervalTicks;
            }
        }

        // Everything in flight arrives within both delays.
        Thread.Sleep(TimeSpan.FromMilliseconds((options.DelayMs * 2) + 500));
        running.Cancel();
        server.Dispose();
        foreach (var client in clients)
        {
            client.Dispose();
        }

        serverThread.Join();
        foreach (var thread in clientThreads)
        {
            thread.Join();
        }

        proxy.Dispose();
        var counts = proxy.Capture();
        SelfTestDirectionResult toServer;
        SelfTestDirectionResult toClient;
        lock (gate)
        {
            toServer = new SelfTestDirectionResult(sent, toServerReceived, toServerErrors.Summarize());
            toClient = new SelfTestDirectionResult(toServerReceived, toClientReceived, toClientErrors.Summarize());
        }

        var passed = Within(toServer) && Within(toClient)
            && counts.ClientToServer.LatenessMs?.P99 < SelfTestOptions.MaximumP99ErrorMs
            && counts.ServerToClient.LatenessMs?.P99 < SelfTestOptions.MaximumP99ErrorMs;
        var result = new SelfTestResult(
            options.DelayMs,
            options.Seconds,
            options.Clients,
            options.PacketsPerSecondPerClient,
            options.PacketBytes,
            highResolution,
            toServer,
            toClient,
            counts,
            passed);
        Write(result, output);
        if (options.OutputPath is not null)
        {
            File.WriteAllText(options.OutputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            output.WriteLine($"Self-test result: {options.OutputPath}");
        }

        return result;
    }

    private static bool Within(SelfTestDirectionResult direction)
    {
        return direction.Sent > 0
            && direction.Received == direction.Sent
            && direction.ErrorMs is { } error
            && error.P99 < SelfTestOptions.MaximumP99ErrorMs;
    }

    private static Thread StartReceiver(string name, Action receive)
    {
        var thread = new Thread(() => receive())
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.AboveNormal
        };
        thread.Start();
        return thread;
    }

    private static void Write(SelfTestResult result, TextWriter output)
    {
        var invariant = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(invariant, $"High-resolution timer: {(result.HighResolutionTimer ? "yes" : "no")}."));
        WriteDirection(output, "client to server", result.ClientToServer);
        WriteDirection(output, "server to client", result.ServerToClient);
        output.WriteLine(string.Create(
            invariant,
            $"The proxy's own lateness against the due time, p99: client to server {result.Proxy.ClientToServer.LatenessMs?.P99 ?? 0:0.000} ms, server to client {result.Proxy.ServerToClient.LatenessMs?.P99 ?? 0:0.000} ms; the process used {result.Proxy.AverageSingleCoreCpuPercent:0.0} % of one core with the test's own threads."));
        output.WriteLine(result.Passed
            ? string.Create(invariant, $"Self-test passed: every packet arrived and the p99 error stays below {SelfTestOptions.MaximumP99ErrorMs:0} ms in both directions.")
            : string.Create(invariant, $"Self-test FAILED: a packet was lost or a p99 error reached {SelfTestOptions.MaximumP99ErrorMs:0} ms."));
    }

    private static void WriteDirection(TextWriter output, string name, SelfTestDirectionResult direction)
    {
        var error = direction.ErrorMs;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {name}: {direction.Received}/{direction.Sent} arrived; error of the applied delay ms: min {error?.Minimum ?? 0:0.000}, p50 {error?.P50 ?? 0:0.000}, p95 {error?.P95 ?? 0:0.000}, p99 {error?.P99 ?? 0:0.000}, max {error?.Maximum ?? 0:0.000}, average {error?.Average ?? 0:0.000}"));
    }
}
