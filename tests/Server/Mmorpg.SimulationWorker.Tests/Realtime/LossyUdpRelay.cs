using System.Net;
using System.Net.Sockets;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A UDP relay on loopback between one test client and the worker: what sits on the path. It loses,
// on request, every datagram from the worker that carries reliable data, so a test can lose a
// reliable message for good while the disconnect, acknowledgements, pings, and snapshots still
// pass; it sees through the seal only with the worker-to-client key that the test hands it. It
// keeps every datagram it relayed, and it sends what a test injects from its own sockets, so the
// worker and the client take an injected datagram as one of the other's.
internal sealed class LossyUdpRelay : IDisposable
{
    // LiteNetLib 2.1.4 packet properties, the low five bits of the first byte.
    private const int PropertyMask = 0x1F;
    private const byte Channeled = 1;
    private const byte ReliableMerged = 2;
    private const byte Disconnect = 8;
    private const byte Merged = 13;

    // A channeled packet names its channel at byte 3 as channel times four plus the delivery, so
    // the delivery is the same rest for every channel of the protocol.
    private const int ChannelIdOffset = 3;
    private const int DeliveryKinds = 4;

    private readonly Socket front = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Socket back = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly IPEndPoint worker;
    private readonly Thread toWorker;
    private readonly Thread toClient;
    private readonly object sync = new();
    private readonly List<byte[]> toWorkerDatagrams = [];
    private readonly List<byte[]> toClientDatagrams = [];
    private volatile IPEndPoint? client;
    private volatile bool loseReliableToClient;
    private volatile bool loseFirstDisconnectToClient;
    private IRealtimeAead? workerToClient;
    private int lostToClient;

    public LossyUdpRelay(int workerPort)
    {
        worker = new IPEndPoint(IPAddress.Loopback, workerPort);
        front.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        back.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        toWorker = new Thread(RelayToWorker) { IsBackground = true, Name = "Lossy relay to worker" };
        toClient = new Thread(RelayToClient) { IsBackground = true, Name = "Lossy relay to client" };
        toWorker.Start();
        toClient.Start();
    }

    // The port the client connects to.
    public int Port => ((IPEndPoint)front.LocalEndPoint!).Port;

    // How many packets from the worker the relay lost.
    public int LostToClient => Volatile.Read(ref lostToClient);

    // The datagrams that the relay passed on, in order.
    public IReadOnlyList<byte[]> ToWorker
    {
        get
        {
            lock (sync)
            {
                return [.. toWorkerDatagrams];
            }
        }
    }

    public IReadOnlyList<byte[]> ToClient
    {
        get
        {
            lock (sync)
            {
                return [.. toClientDatagrams];
            }
        }
    }

    // From now on every datagram from the worker that carries reliable data is lost; a sealed one
    // is opened with the worker-to-client key of the ticket to look inside.
    public void LoseReliableToClient(string ticket)
    {
        using (var keys = RealtimeTestTickets.KeysOf(ticket))
        {
            Volatile.Write(ref workerToClient, new RealtimeManagedChaCha20Poly1305(keys.WorkerToClientKey));
        }

        loseReliableToClient = true;
    }

    // The first disconnect datagram from the worker from now on is lost, so the client can only
    // learn of the disconnect from LiteNetLib's next send of it.
    public void LoseFirstDisconnectToClient(string ticket)
    {
        using (var keys = RealtimeTestTickets.KeysOf(ticket))
        {
            Volatile.Write(ref workerToClient, new RealtimeManagedChaCha20Poly1305(keys.WorkerToClientKey));
        }

        loseFirstDisconnectToClient = true;
    }

    // Sends the datagram to the worker as if the client sent it.
    public void InjectToWorker(byte[] datagram)
    {
        back.SendTo(datagram, worker);
    }

    // Sends the datagram to the client as if the worker sent it.
    public void InjectToClient(byte[] datagram)
    {
        var destination = client ?? throw new InvalidOperationException("No client used the relay yet.");
        front.SendTo(datagram, destination);
    }

    public void Dispose()
    {
        front.Dispose();
        back.Dispose();
        toWorker.Join(TimeSpan.FromSeconds(5));
        toClient.Join(TimeSpan.FromSeconds(5));
        workerToClient?.Dispose();
    }

    // Whether a LiteNetLib packet carries reliable data: a reliable channeled packet, merged
    // reliable packets, or a merged datagram that holds one of them.
    internal static bool CarriesReliableData(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
        {
            return false;
        }

        switch (packet[0] & PropertyMask)
        {
            case Channeled:
                return packet.Length > ChannelIdOffset
                    && (packet[ChannelIdOffset] % DeliveryKinds) is 0 or 2;
            case ReliableMerged:
                return true;
            case Merged:
                var position = 1;
                while (position + 2 <= packet.Length)
                {
                    var size = BitConverter.ToUInt16(packet.Slice(position, 2));
                    position += 2;
                    if (size == 0 || position + size > packet.Length)
                    {
                        return false;
                    }

                    if (CarriesReliableData(packet.Slice(position, size)))
                    {
                        return true;
                    }

                    position += size;
                }

                return false;
            default:
                return false;
        }
    }

    private bool DatagramCarriesReliableData(byte[] buffer, int length)
    {
        return TryReadPacket(buffer, length, out var packet) && CarriesReliableData(packet);
    }

    private bool DatagramIsADisconnect(byte[] buffer, int length)
    {
        return TryReadPacket(buffer, length, out var packet)
            && packet.Length > 0
            && (packet[0] & PropertyMask) == Disconnect;
    }

    // The LiteNetLib packet inside a datagram from the worker: a clear one after its marker, a
    // sealed one opened on a copy, so the datagram itself passes unchanged.
    private bool TryReadPacket(byte[] buffer, int length, out byte[] packet)
    {
        packet = [];
        if (length < 1)
        {
            return false;
        }

        if (buffer[0] == RealtimeDatagram.ClearMarker)
        {
            packet = buffer[1..length];
            return true;
        }

        var aead = Volatile.Read(ref workerToClient);
        var copy = buffer[..length];
        var opened = length;
        if (aead is null
            || !RealtimeDatagram.TryReadCounter(copy, length, out var counter)
            || !RealtimeDatagram.TryOpen(aead, counter, copy, ref opened))
        {
            return false;
        }

        packet = copy[..opened];
        return true;
    }

    private void RelayToWorker()
    {
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            int length;
            try
            {
                length = front.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows reports an earlier send to a closed port on the next receive.
                continue;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return;
            }

            client = (IPEndPoint)from;
            lock (sync)
            {
                toWorkerDatagrams.Add(buffer[..length]);
            }

            try
            {
                back.SendTo(buffer.AsSpan(0, length), worker);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private void RelayToClient()
    {
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            int length;
            try
            {
                length = back.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return;
            }

            var destination = client;
            if (destination is null)
            {
                continue;
            }

            if (loseReliableToClient && DatagramCarriesReliableData(buffer, length))
            {
                Interlocked.Increment(ref lostToClient);
                continue;
            }

            if (loseFirstDisconnectToClient && DatagramIsADisconnect(buffer, length))
            {
                loseFirstDisconnectToClient = false;
                Interlocked.Increment(ref lostToClient);
                continue;
            }

            lock (sync)
            {
                toClientDatagrams.Add(buffer[..length]);
            }

            try
            {
                front.SendTo(buffer.AsSpan(0, length), destination);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }
}
