using System.Net;
using System.Net.Sockets;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A UDP relay on loopback between one test client and the worker that loses, on request, every
// packet from the worker that carries reliable data, so a test can lose a reliable message for
// good while the disconnect, acknowledgements, pings, and snapshots still pass.
internal sealed class LossyUdpRelay : IDisposable
{
    // LiteNetLib 2.1.4 packet properties, the low five bits of the first byte.
    private const int PropertyMask = 0x1F;
    private const byte Channeled = 1;
    private const byte ReliableMerged = 2;
    private const byte Merged = 13;

    // A channeled packet names its channel at byte 3 as channel times four plus the delivery.
    private const int ChannelIdOffset = 3;
    private const int DeliveryKinds = 4;

    private readonly Socket front = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Socket back = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly IPEndPoint worker;
    private readonly Thread toWorker;
    private readonly Thread toClient;
    private volatile IPEndPoint? client;
    private volatile bool loseReliableToClient;
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

    // From now on every packet from the worker that carries reliable data is lost.
    public void LoseReliableToClient()
    {
        loseReliableToClient = true;
    }

    public void Dispose()
    {
        front.Dispose();
        back.Dispose();
        toWorker.Join(TimeSpan.FromSeconds(5));
        toClient.Join(TimeSpan.FromSeconds(5));
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

            if (loseReliableToClient && CarriesReliableData(buffer.AsSpan(0, length)))
            {
                Interlocked.Increment(ref lostToClient);
                continue;
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
