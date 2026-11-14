using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Mmorpg.Tools.NetworkImpairmentProxy;

public sealed record ImpairmentProxyOptions(
    IPEndPoint Listen,
    IPEndPoint Forward,
    long Seed,
    TimeSpan IdleTimeout,
    int MaximumClients,
    bool AllowNonLoopback)
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);
    public const int DefaultMaximumClients = 2048;

    // How long before a due time each scheduler thread stops sleeping and spins.
    public TimeSpan Spin { get; init; } = PacketScheduler.DefaultSpin;

    // Like the load generator, the proxy stays on this machine unless a flag says otherwise.
    public void RequireLoopback()
    {
        if (AllowNonLoopback)
        {
            return;
        }

        if (!IPAddress.IsLoopback(Listen.Address))
        {
            throw new NetworkImpairmentException(
                $"The proxy refuses to listen on {Listen}, which is not a loopback address; --allow-non-loopback allows it.");
        }

        if (!IPAddress.IsLoopback(Forward.Address))
        {
            throw new NetworkImpairmentException(
                $"The proxy refuses to forward to {Forward}, which is not a loopback address; --allow-non-loopback allows it.");
        }
    }
}

// A UDP proxy between clients and one server that impairs both directions by a network profile.
// It listens on one port, opens one upstream socket per client address, so the server sees every
// client apart, and forwards each packet after the profile's delay, or drops or duplicates it.
// It never reads or changes a payload. Each direction has its own scheduler thread.
public sealed class ImpairmentProxy : IDisposable
{
    // Any UDP datagram fits.
    private const int MaximumDatagramBytes = 65_536;
    private const int SocketBufferBytes = 4 * 1024 * 1024;

    // Windows reports an ICMP port unreachable as a reset on the next receive of the socket that
    // sent; a client that went away must not end the receive loop of every other client.
    private const int SioUdpConnectionReset = unchecked((int)0x9800000C);

    private readonly ImpairmentProxyOptions options;
    private readonly NetworkProfile profile;
    private readonly ConcurrentDictionary<SocketAddress, ClientFlow> flows = new();
    private readonly DirectionCounters toServerCounters = new();
    private readonly DirectionCounters toClientCounters = new();
    private readonly PacketScheduler toServer;
    private readonly PacketScheduler toClient;
    private readonly CancellationTokenSource stop = new();
    private readonly Socket listener;
    private readonly DateTime startedAt = DateTime.UtcNow;
    private readonly ProcessorTime processorTime = new();
    private DateTime? endedAt;
    private Timer? idleSweep;
    private Task? listenLoop;
    private int clientsSeen;
    private int clientsTimedOut;
    private int clientsRefused;
    private int maximumActiveClients;
    private bool disposed;

    public ImpairmentProxy(ImpairmentProxyOptions options, NetworkProfile profile)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        options.RequireLoopback();
        toServer = new PacketScheduler(SendToServer, toServerCounters, spin: options.Spin);
        toClient = new PacketScheduler(SendToClient, toClientCounters, spin: options.Spin);
        listener = CreateSocket(options.Listen.AddressFamily);
        try
        {
            listener.Bind(options.Listen);
        }
        catch (SocketException exception)
        {
            listener.Dispose();
            throw new NetworkImpairmentException($"The proxy cannot listen on UDP {options.Listen}: {exception.Message}");
        }
    }

    // The bound listen address, with the port the system chose for port 0.
    public IPEndPoint ListenEndPoint => (IPEndPoint)listener.LocalEndPoint!;

    public int ActiveClients => flows.Count;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        toServer.Start("Proxy to server");
        toClient.Start("Proxy to client");
        listenLoop = Task.Run(ReceiveFromClientsAsync);
        // A quarter of the idle timeout, at most a second, so a client goes soon after its time.
        var sweep = TimeSpan.FromTicks(Math.Clamp(options.IdleTimeout.Ticks / 4, TimeSpan.TicksPerMillisecond * 50, TimeSpan.TicksPerSecond));
        idleSweep = new Timer(_ => SweepIdleClients(), null, sweep, sweep);
    }

    public ProxyCountsReport Capture()
    {
        var ended = endedAt ?? DateTime.UtcNow;
        return new ProxyCountsReport(
            profile.Name,
            options.Seed,
            startedAt,
            ended,
            Math.Round((ended - startedAt).TotalSeconds, 3),
            options.Listen.ToString(),
            options.Forward.ToString(),
            Volatile.Read(ref clientsSeen),
            Volatile.Read(ref clientsTimedOut),
            Volatile.Read(ref clientsRefused),
            Volatile.Read(ref maximumActiveClients),
            toServerCounters.Capture(),
            toClientCounters.Capture())
        {
            ProcessorSeconds = Math.Round(processorTime.Elapsed(), 3)
        };
    }

    // One line of what happened so far, for a periodic log.
    public string Progress()
    {
        var up = toServerCounters.Capture();
        var down = toClientCounters.Capture();
        return FormattableString.Invariant(
            $"{flows.Count} clients; to server {up.PacketsForwarded}/{up.PacketsReceived} forwarded, {up.DroppedByRule} dropped, p99 lateness {up.LatenessMs?.P99 ?? 0:0.000} ms; to client {down.PacketsForwarded}/{down.PacketsReceived} forwarded, {down.DroppedByRule} dropped, p99 lateness {down.LatenessMs?.P99 ?? 0:0.000} ms; {toServer.Pending + toClient.Pending} waiting.");
    }

    // Stops receiving, discards what still waits, and closes every socket.
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stop.Cancel();
        idleSweep?.Dispose();
        listener.Dispose();
        try
        {
            listenLoop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop ends with the socket; its exception says nothing new.
        }

        foreach (var flow in flows.Values)
        {
            flow.Close();
        }

        toServer.Dispose();
        toClient.Dispose();
        endedAt = DateTime.UtcNow;
        stop.Dispose();
    }

    private static Socket CreateSocket(AddressFamily family)
    {
        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = SocketBufferBytes,
            SendBufferSize = SocketBufferBytes
        };
        if (OperatingSystem.IsWindows())
        {
            socket.IOControl(SioUdpConnectionReset, [0, 0, 0, 0], null);
        }

        return socket;
    }

    private async Task ReceiveFromClientsAsync()
    {
        var buffer = GC.AllocateUninitializedArray<byte>(MaximumDatagramBytes, pinned: true);
        var address = new SocketAddress(listener.AddressFamily);
        while (!stop.IsCancellationRequested)
        {
            int length;
            try
            {
                length = await listener.ReceiveFromAsync(buffer, SocketFlags.None, address, stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // A reset or a too large datagram concerns one packet, not the loop.
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            var flow = FindOrOpenFlow(address, now);
            if (flow is not null)
            {
                Handle(flow, ProxyDirection.ClientToServer, buffer.AsSpan(0, length), now);
            }
        }
    }

    private async Task ReceiveFromServerAsync(ClientFlow flow)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(MaximumDatagramBytes, pinned: true);
        while (!flow.IsClosed && !stop.IsCancellationRequested)
        {
            int length;
            try
            {
                length = await flow.Upstream.ReceiveAsync(buffer, SocketFlags.None, stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (flow.IsClosed)
                {
                    return;
                }

                continue;
            }

            Handle(flow, ProxyDirection.ServerToClient, buffer.AsSpan(0, length), Stopwatch.GetTimestamp());
        }
    }

    // Only the listen loop opens flows, so a client never gets two.
    private ClientFlow? FindOrOpenFlow(SocketAddress address, long now)
    {
        if (flows.TryGetValue(address, out var flow))
        {
            return flow;
        }

        if (flows.Count >= options.MaximumClients)
        {
            Interlocked.Increment(ref clientsRefused);
            return null;
        }

        var key = new SocketAddress(address.Family, address.Size);
        address.Buffer.Span[..address.Size].CopyTo(key.Buffer.Span);
        var upstream = CreateSocket(options.Forward.AddressFamily);
        try
        {
            upstream.Bind(new IPEndPoint(
                IPAddress.IsLoopback(options.Forward.Address)
                    ? options.Forward.Address
                    : options.Forward.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                0));
            upstream.Connect(options.Forward);
        }
        catch (SocketException)
        {
            upstream.Dispose();
            Interlocked.Increment(ref clientsRefused);
            return null;
        }

        var number = Interlocked.Increment(ref clientsSeen);
        flow = new ClientFlow(
            number,
            key,
            upstream,
            now,
            new DirectionImpairment(profile.ClientToServer, ImpairmentRandom.DeriveSeed(options.Seed, number, 0), Stopwatch.Frequency),
            new DirectionImpairment(profile.ServerToClient, ImpairmentRandom.DeriveSeed(options.Seed, number, 1), Stopwatch.Frequency));
        flows[key] = flow;
        var active = flows.Count;
        if (active > Volatile.Read(ref maximumActiveClients))
        {
            Volatile.Write(ref maximumActiveClients, active);
        }

        _ = Task.Run(() => ReceiveFromServerAsync(flow));
        return flow;
    }

    private void Handle(ClientFlow flow, ProxyDirection direction, ReadOnlySpan<byte> payload, long now)
    {
        flow.Touch(now);
        var counters = direction == ProxyDirection.ClientToServer ? toServerCounters : toClientCounters;
        var scheduler = direction == ProxyDirection.ClientToServer ? toServer : toClient;
        counters.RecordReceived(payload.Length);
        var impairment = direction == ProxyDirection.ClientToServer ? flow.ToServer : flow.ToClient;
        var decision = impairment.Decide(now - flow.StartTicks);
        if (!decision.IsForwarded)
        {
            counters.RecordDropped(decision.Fate);
            return;
        }

        scheduler.Schedule(DelayedPacket.Copy(flow, payload, now, now + decision.DelayTicks));
        if (decision.DuplicateDelayTicks is { } duplicateDelay)
        {
            counters.RecordDuplicated();
            scheduler.Schedule(DelayedPacket.Copy(flow, payload, now, now + duplicateDelay));
        }
    }

    private bool SendToServer(DelayedPacket packet)
    {
        var flow = (ClientFlow)packet.Flow;
        try
        {
            flow.Upstream.Send(packet.Payload, SocketFlags.None);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    private bool SendToClient(DelayedPacket packet)
    {
        var flow = (ClientFlow)packet.Flow;
        try
        {
            listener.SendTo(packet.Payload, SocketFlags.None, flow.ClientAddress);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    // A client that sent nothing and got nothing for the idle timeout is gone; its upstream
    // socket closes, so the server sees it no more.
    private void SweepIdleClients()
    {
        var now = Stopwatch.GetTimestamp();
        var idleTicks = ProxyTime.Ticks(options.IdleTimeout.TotalMilliseconds);
        foreach (var pair in flows)
        {
            if (now - pair.Value.LastActivityTicks > idleTicks && flows.TryRemove(pair.Key, out var flow))
            {
                flow.Close();
                Interlocked.Increment(ref clientsTimedOut);
            }
        }
    }

    // The processor time of this process since the proxy was made, which the proxy's threads
    // dominate; it shows how much of the machine a run's proxy took.
    private sealed class ProcessorTime
    {
        private readonly TimeSpan start = Process.GetCurrentProcess().TotalProcessorTime;

        public double Elapsed()
        {
            return (Process.GetCurrentProcess().TotalProcessorTime - start).TotalSeconds;
        }
    }

    private sealed class ClientFlow(
        int number,
        SocketAddress clientAddress,
        Socket upstream,
        long startTicks,
        DirectionImpairment toServer,
        DirectionImpairment toClient)
    {
        private long lastActivityTicks = startTicks;
        private int closed;

        public int Number { get; } = number;

        public SocketAddress ClientAddress { get; } = clientAddress;

        public Socket Upstream { get; } = upstream;

        public long StartTicks { get; } = startTicks;

        public DirectionImpairment ToServer { get; } = toServer;

        public DirectionImpairment ToClient { get; } = toClient;

        public long LastActivityTicks => Interlocked.Read(ref lastActivityTicks);

        public bool IsClosed => Volatile.Read(ref closed) == 1;

        public void Touch(long now)
        {
            Interlocked.Exchange(ref lastActivityTicks, now);
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0)
            {
                Upstream.Dispose();
            }
        }
    }
}
