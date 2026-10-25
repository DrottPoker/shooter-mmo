using System.Diagnostics;
using System.Net;
using LiteNetLib;
using Mmorpg.GameProtocol;

namespace Mmorpg.Tools.SimulationBotClient;

// Connects one SimulationBotSession to SimulationWorker over LiteNetLib in manual mode, so the
// caller's poll loop drives receiving, the session, and sending.
public sealed class SimulationBotClient : IDisposable
{
    // The Unity client's default. The session keeps its own join and leave deadlines, so a
    // short join timeout does not drop joined bots during a brief worker stall.
    private static readonly TimeSpan TransportTimeout = TimeSpan.FromSeconds(10);

    private readonly SimulationBotConnectionOptions connectionOptions;
    private readonly SimulationBotSession session;
    private readonly EventBasedNetListener listener = new();
    private readonly NetManager client;
    private long lastPollTimestamp;
    private bool transportClosed;
    private bool disposed;

    public SimulationBotClient(
        SimulationBotConnectionOptions connectionOptions,
        SimulationBotIdentity identity,
        ISimulationBotInputSource inputSource,
        Action<double>? inputAcknowledged = null,
        ISimulationBotRealtimeObserver? realtimeObserver = null)
    {
        this.connectionOptions = connectionOptions
            ?? throw new ArgumentNullException(nameof(connectionOptions));
        if (string.IsNullOrWhiteSpace(connectionOptions.Host)
            || connectionOptions.UdpPort is < 1 or > ushort.MaxValue
            || connectionOptions.JoinAndLeaveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Simulation bot connection options are invalid.",
                nameof(connectionOptions));
        }

        session = new SimulationBotSession(
            identity,
            connectionOptions.JoinAndLeaveTimeout,
            inputSource,
            inputAcknowledged,
            realtimeObserver);
        client = new NetManager(listener)
        {
            ChannelsCount = RealtimeProtocol.ChannelCount,
            DisconnectTimeout = (int)TransportTimeout.TotalMilliseconds,
            MaxPacketPerManualReceive = 256
        };

        listener.PeerConnectedEvent += HandleConnected;
        listener.PeerDisconnectedEvent += HandleDisconnected;
        listener.NetworkReceiveEvent += HandleNetworkReceive;
    }

    public SimulationBotClientState State => session.State;

    public void Start()
    {
        ThrowIfDisposed();
        var now = Stopwatch.GetTimestamp();
        session.Start(now);
        lastPollTimestamp = now;
        if (!client.Start(IPAddress.Any, IPAddress.IPv6Any, 0, manualMode: true))
        {
            session.Fail(
                "client_start_failed",
                "LiteNetLib could not start the simulation bot transport.");
            return;
        }

        if (client.Connect(
                connectionOptions.Host,
                connectionOptions.UdpPort,
                RealtimeProtocol.ConnectionKey) is null)
        {
            session.Fail(
                "connection_start_failed",
                "LiteNetLib could not create the simulation bot connection.");
        }
    }

    // Receives, sends the inputs that are due, then lets the transport send everything queued,
    // so an input leaves in the same poll instead of waiting for the next one.
    public void Poll(long nowTimestamp)
    {
        ThrowIfDisposed();
        client.PollEvents();
        session.Update(nowTimestamp);
        // An ended session gives its worker connection back at once, not only when the caller
        // disposes the client, so a failed bot stops holding a joined character.
        if (!transportClosed
            && session.State is SimulationBotClientState.Completed or SimulationBotClientState.Failed)
        {
            transportClosed = true;
            client.DisconnectAll();
        }

        client.ManualUpdate(
            (float)Math.Max(
                0d,
                Stopwatch.GetElapsedTime(lastPollTimestamp, nowTimestamp).TotalMilliseconds));
        lastPollTimestamp = nowTimestamp;
    }

    public void BeginLeave(long nowTimestamp)
    {
        ThrowIfDisposed();
        session.BeginLeave(nowTimestamp);
    }

    public bool SendItemOperation(RealtimeItemOperationIntent intent)
    {
        ThrowIfDisposed();
        return session.SendItemOperation(intent);
    }

    public bool SendCorpseInteraction(RealtimeCorpseInteractionIntent intent)
    {
        ThrowIfDisposed();
        return session.SendCorpseInteraction(intent);
    }

    public SimulationBotClientSnapshot Capture()
    {
        return session.Capture();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        client.Stop();
    }

    private void HandleConnected(NetPeer peer)
    {
        session.OnConnected(new PeerTransport(peer));
    }

    private void HandleDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        session.OnDisconnected(disconnectInfo.Reason.ToString());
    }

    private void HandleNetworkReceive(
        NetPeer peer,
        NetPacketReader reader,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        try
        {
            var packet = reader.GetRemainingBytes();
            if (TryGetRealtimeDelivery(deliveryMethod, out var delivery))
            {
                session.Receive(packet, channel, delivery, Stopwatch.GetTimestamp());
            }
            else
            {
                session.Fail(
                    "invalid_delivery",
                    $"SimulationWorker sent a packet with {deliveryMethod} delivery, which the protocol never uses.");
            }
        }
        finally
        {
            reader.Recycle();
        }
    }

    private static bool TryGetRealtimeDelivery(
        DeliveryMethod deliveryMethod,
        out RealtimeDelivery delivery)
    {
        delivery = deliveryMethod switch
        {
            DeliveryMethod.ReliableOrdered => RealtimeDelivery.ReliableOrdered,
            DeliveryMethod.Sequenced => RealtimeDelivery.Sequenced,
            DeliveryMethod.Unreliable => RealtimeDelivery.Unreliable,
            _ => default
        };
        return delivery != default;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class PeerTransport(NetPeer peer) : ISimulationBotTransport
    {
        public void Send(byte[] packet, byte channel, RealtimeDelivery delivery)
        {
            peer.Send(packet, channel, delivery switch
            {
                RealtimeDelivery.ReliableOrdered => DeliveryMethod.ReliableOrdered,
                RealtimeDelivery.Sequenced => DeliveryMethod.Sequenced,
                RealtimeDelivery.Unreliable => DeliveryMethod.Unreliable,
                _ => throw new ArgumentOutOfRangeException(nameof(delivery))
            });
        }
    }
}
