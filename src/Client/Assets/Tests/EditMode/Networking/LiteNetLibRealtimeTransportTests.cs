using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Layers;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Drives the client's LiteNetLib transport against a LiteNetLib server with the worker's
    // sealing layer over loopback, for what a fake transport cannot show: the data that a
    // disconnect carries, and how the client's datagrams switch from clear to sealed.
    public sealed class LiteNetLibRealtimeTransportTests
    {
        private const int TimeoutMilliseconds = 5000;

        private readonly List<byte[]> received = new List<byte[]>();
        private EventBasedNetListener serverListener;
        private RealtimeWorkerTransportSecurity serverSecurity;
        private NetManager server;
        private NetPeer serverPeer;
        private LiteNetLibRealtimeTransport transport;

        [SetUp]
        public void SetUp()
        {
            serverListener = new EventBasedNetListener();
            serverSecurity = new RealtimeWorkerTransportSecurity();
            server = new NetManager(serverListener, new WorkerSealingLayer(serverSecurity))
            {
                ChannelsCount = RealtimeProtocol.ChannelCount,
                MtuOverride = RealtimeDatagram.LiteNetLibMtu
            };
            serverListener.ConnectionRequestEvent += request => request.AcceptIfKey(RealtimeProtocol.ConnectionKey);
            serverListener.PeerConnectedEvent += peer => serverPeer = peer;
            serverListener.PeerDisconnectedEvent += (peer, info) => serverSecurity.Remove(peer);
            Assert.That(server.Start(IPAddress.Loopback, IPAddress.IPv6None, 0), Is.True);
            transport = new LiteNetLibRealtimeTransport();
            received.Clear();
            transport.Received += OnReceived;
        }

        [TearDown]
        public void TearDown()
        {
            transport.Received -= OnReceived;
            transport.Stop();
            server.Stop();
            serverSecurity.Dispose();
        }

        [Test]
        public void ADisconnectHandsOnTheDataTheServerPutIntoIt()
        {
            using (var keys = NewKeys())
            {
                Connect(keys);
                serverSecurity.Install(serverPeer, keys);
            }

            var reason = RealtimeProtocol.EncodeServerDisconnect("server_shutdown", "The worker stops.");

            server.DisconnectPeer(serverPeer, reason);
            var (disconnectReason, data) = WaitForDisconnect();

            Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.RemoteConnectionClose));
            Assert.That(data, Is.EqualTo(reason));
            Assert.That(transport.Security.IsSealing, Is.True);
        }

        // A disconnect without data hands on an empty array, round after round on one transport,
        // each round with the keys of its own join.
        [Test]
        public void ADisconnectWithoutDataHandsOnNothing()
        {
            for (var round = 0; round < 3; round++)
            {
                using (var keys = NewKeys())
                {
                    Connect(keys);
                    serverSecurity.Install(serverPeer, keys);
                }

                server.DisconnectPeer(serverPeer);
                var (disconnectReason, data) = WaitForDisconnect();

                Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.RemoteConnectionClose));
                Assert.That(data, Is.Empty);
                transport.Stop();
            }
        }

        // Before the worker's keys a clear packet carries no channel's data to the client; once
        // the worker seals, the client's first sealed datagram switches it, and it seals what it
        // sends and drops what still comes in clear.
        [Test]
        public void TheClientSealsOnceTheWorkerDoesAndThenDropsClearDatagrams()
        {
            using (var keys = NewKeys())
            {
                Connect(keys);
                // Unreliable, so LiteNetLib never sends it again once the keys are in.
                var forged = RealtimeProtocol.EncodeGameServiceAvailabilityChanged(false);
                serverPeer.Send(forged, RealtimeProtocol.UnreliableReceiveChannel, DeliveryMethod.Unreliable);
                PollUntil(() => transport.Security.Counters.DroppedClearNotAllowed > 0, "the clear packet to be dropped");
                Assert.That(received, Is.Empty);
                Assert.That(transport.Security.IsSealing, Is.False);

                serverSecurity.Install(serverPeer, keys);
                var accepted = RealtimeProtocol.EncodeGameServiceAvailabilityChanged(true);
                serverPeer.Send(accepted, RealtimeProtocol.OwnerChannel, DeliveryMethod.ReliableOrdered);
                PollUntil(() => received.Count > 0, "the sealed message");
                Assert.That(transport.Security.IsSealing, Is.True);

                var sealedBefore = serverSecurity.Counters.SealedReceived;
                transport.Send(RealtimeProtocol.EncodeLeaveRequest(Guid.NewGuid().ToString("D")), RealtimeProtocol.OwnerChannel, DeliveryMethod.ReliableOrdered);
                transport.TriggerUpdate();
                PollUntil(() => serverSecurity.Counters.SealedReceived > sealedBefore, "the client's sealed datagram");

                // The worker's keys go, so it sends in clear again, which the client drops.
                serverSecurity.Remove(serverPeer);
                serverPeer.Send(accepted, RealtimeProtocol.OwnerChannel, DeliveryMethod.ReliableOrdered);
                PollUntil(() => transport.Security.Counters.DroppedClearAfterSealing > 0, "the clear datagram to be dropped");
                Assert.That(received.Count, Is.EqualTo(1));
            }
        }

        private static RealtimeTransportKeys NewKeys()
        {
            return RealtimeTransportKeys.Derive(RealtimeTransportKeys.CreateTransportKey());
        }

        private void OnReceived(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            received.Add(packet);
        }

        private void Connect(RealtimeTransportKeys keys)
        {
            serverPeer = null;
            var connected = false;
            transport.Connected += OnConnected;
            try
            {
                Assert.That(transport.Start(TimeoutMilliseconds), Is.True);
                transport.Connect("127.0.0.1", server.LocalPort, RealtimeProtocol.ConnectionKey, keys);
                PollUntil(() => connected && serverPeer != null, "the transport to connect");
            }
            finally
            {
                transport.Connected -= OnConnected;
            }

            void OnConnected()
            {
                connected = true;
            }
        }

        private (DisconnectReason Reason, byte[] Data) WaitForDisconnect()
        {
            DisconnectReason? reason = null;
            byte[] data = null;
            void OnDisconnected(DisconnectReason disconnectReason, byte[] disconnectData)
            {
                reason = disconnectReason;
                data = disconnectData;
            }

            transport.Disconnected += OnDisconnected;
            try
            {
                PollUntil(() => reason.HasValue, "the disconnect");
            }
            finally
            {
                transport.Disconnected -= OnDisconnected;
            }

            return (reason.Value, data);
        }

        private void PollUntil(Func<bool> condition, string description)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > TimeoutMilliseconds)
                {
                    Assert.Fail("Timed out waiting for " + description + ".");
                }

                transport.PollEvents();
                server.PollEvents();
                Thread.Sleep(5);
            }
        }

        // The worker's side of the sealed transport, as SimulationWorker installs it.
        private sealed class WorkerSealingLayer : PacketLayerBase
        {
            private readonly RealtimeWorkerTransportSecurity security;

            public WorkerSealingLayer(RealtimeWorkerTransportSecurity security)
                : base(RealtimeDatagram.MaximumOverhead)
            {
                this.security = security;
            }

            public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
            {
                security.ProcessInbound(endPoint, data, ref length);
            }

            public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
            {
                security.ProcessOutbound(endPoint, data, ref offset, ref length);
            }
        }
    }
}
