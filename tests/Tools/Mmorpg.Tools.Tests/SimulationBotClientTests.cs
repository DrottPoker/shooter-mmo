using System.Diagnostics;
using System.Net;
using LiteNetLib;
using LiteNetLib.Layers;
using Mmorpg.GameProtocol;
using Mmorpg.Tools.SimulationBotClient;
using HeadlessSimulationBotClient = Mmorpg.Tools.SimulationBotClient.SimulationBotClient;

namespace Mmorpg.Tools.Tests;

// Drives the LiteNetLib transport of a bot against a loopback server that plays the worker, with
// the worker's sealing layer.
public sealed class SimulationBotClientTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void JoinsWithAProofSealsOnceTheWorkerDoesAndReleasesTheConnectionAsSoonAsTheSessionFails()
    {
        var worker = new LoopbackWorker();
        var ticketId = Guid.NewGuid();
        var transportKey = RealtimeTransportKeys.CreateTransportKey();
        try
        {
            using var bot = new HeadlessSimulationBotClient(
                new SimulationBotConnectionOptions(
                    "127.0.0.1",
                    worker.Port,
                    TimeSpan.FromSeconds(10)),
                new SimulationBotIdentity(
                    1,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "Active Bot 1",
                    ticketId,
                    transportKey,
                    "local-shard-2",
                    "development-world-1"),
                new StandingInputSource());
            bot.Start();

            PollUntil(bot, worker, () => worker.Received is not null);
            Assert.Equal(SimulationBotClientState.Joining, bot.State);
            Assert.Equal(RealtimeProtocol.SessionChannel, worker.ReceivedChannel);
            Assert.Equal(DeliveryMethod.ReliableOrdered, worker.ReceivedDelivery);
            Assert.True(RealtimeProtocol.TryDecodeJoinRequest(worker.Received!, out var request, out _));
            Assert.Equal(ticketId, request.TicketId);
            using var keys = RealtimeTransportKeys.Derive(transportKey);
            Assert.True(RealtimeJoinProof.Verify(
                keys.ProofKey,
                RealtimeProtocol.Version,
                ticketId,
                "local-shard-2",
                request.ClientNonce,
                request.Proof));
            Assert.False(bot.IsSealing);

            // The worker seals from the moment it has the keys; the bot follows on the first
            // sealed datagram.
            worker.Security.Install(worker.Peer!, keys);
            worker.Peer!.Send(
                RealtimeProtocol.EncodeServerDisconnect("worker_stopping", "The worker stops."),
                RealtimeProtocol.SessionChannel,
                DeliveryMethod.ReliableOrdered);
            PollUntil(bot, worker, () => worker.Disconnected);

            Assert.True(bot.IsSealing);
            Assert.True(bot.TransportCounters.SealedReceived > 0);
            Assert.True(worker.Security.Counters.SealedReceived > 0);
            Assert.Equal(0, bot.TransportCounters.Dropped);
            Assert.Equal(SimulationBotClientState.Failed, bot.State);
            Assert.Equal("worker_stopping", bot.Capture().FailureCode);
        }
        finally
        {
            worker.Stop();
        }
    }

    private static void PollUntil(
        HeadlessSimulationBotClient bot,
        LoopbackWorker worker,
        Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(PollTimeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            Assert.True(Stopwatch.GetTimestamp() < deadline, "The loopback exchange timed out.");
            bot.Poll(Stopwatch.GetTimestamp());
            worker.Poll();
            Thread.Sleep(1);
        }
    }

    private sealed class LoopbackWorker
    {
        private readonly EventBasedNetListener listener = new();
        private readonly NetManager server;

        public LoopbackWorker()
        {
            server = new NetManager(listener, new WorkerSealingLayer(Security))
            {
                ChannelsCount = RealtimeProtocol.ChannelCount,
                MtuOverride = RealtimeDatagram.LiteNetLibMtu
            };
            listener.ConnectionRequestEvent += request => request.AcceptIfKey(RealtimeProtocol.ConnectionKey);
            listener.PeerConnectedEvent += peer => Peer = peer;
            listener.PeerDisconnectedEvent += (_, _) => Disconnected = true;
            listener.NetworkReceiveEvent += (_, reader, channel, delivery) =>
            {
                Received = reader.GetRemainingBytes();
                ReceivedChannel = channel;
                ReceivedDelivery = delivery;
                reader.Recycle();
            };
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
        }

        public int Port => server.LocalPort;

        public RealtimeWorkerTransportSecurity Security { get; } = new();

        public NetPeer? Peer { get; private set; }

        public byte[]? Received { get; private set; }

        public byte ReceivedChannel { get; private set; }

        public DeliveryMethod ReceivedDelivery { get; private set; }

        public bool Disconnected { get; private set; }

        public void Poll()
        {
            server.PollEvents();
        }

        public void Stop()
        {
            server.Stop();
            Security.Dispose();
        }
    }

    private sealed class WorkerSealingLayer(RealtimeWorkerTransportSecurity security)
        : PacketLayerBase(RealtimeDatagram.MaximumOverhead)
    {
        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            security.ProcessInbound(endPoint, data, ref length);
        }

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            security.ProcessOutbound(endPoint, data, ref offset, ref length);
        }
    }

    private sealed class StandingInputSource : ISimulationBotInputSource
    {
        public RealtimeMovementPath? SelectPath(
            uint clientTick,
            RealtimeJoinAccepted joinedSession,
            SimulationBotPosition position)
        {
            return null;
        }
    }
}
