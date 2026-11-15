using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using LiteNetLib;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Drives the client's LiteNetLib transport against a LiteNetLib server over loopback, for
    // what a fake transport cannot show: the data that a disconnect carries.
    public sealed class LiteNetLibRealtimeTransportTests
    {
        private const int TimeoutMilliseconds = 5000;

        private EventBasedNetListener serverListener;
        private NetManager server;
        private NetPeer serverPeer;
        private LiteNetLibRealtimeTransport transport;

        [SetUp]
        public void SetUp()
        {
            serverListener = new EventBasedNetListener();
            server = new NetManager(serverListener) { ChannelsCount = RealtimeProtocol.ChannelCount };
            serverListener.ConnectionRequestEvent += request => request.AcceptIfKey(RealtimeProtocol.ConnectionKey);
            serverListener.PeerConnectedEvent += peer => serverPeer = peer;
            Assert.That(server.Start(IPAddress.Loopback, IPAddress.IPv6None, 0), Is.True);
            transport = new LiteNetLibRealtimeTransport();
        }

        [TearDown]
        public void TearDown()
        {
            transport.Stop();
            server.Stop();
        }

        [Test]
        public void ADisconnectHandsOnTheDataTheServerPutIntoIt()
        {
            Connect();
            var reason = RealtimeProtocol.EncodeServerDisconnect("server_shutdown", "The worker stops.");

            server.DisconnectPeer(serverPeer, reason);
            var (disconnectReason, data) = WaitForDisconnect();

            Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.RemoteConnectionClose));
            Assert.That(data, Is.EqualTo(reason));
        }

        // A disconnect without data hands on an empty array, round after round on one transport.
        [Test]
        public void ADisconnectWithoutDataHandsOnNothing()
        {
            for (var round = 0; round < 3; round++)
            {
                Connect();

                server.DisconnectPeer(serverPeer);
                var (disconnectReason, data) = WaitForDisconnect();

                Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.RemoteConnectionClose));
                Assert.That(data, Is.Empty);
                transport.Stop();
            }
        }

        private void Connect()
        {
            serverPeer = null;
            var connected = false;
            transport.Connected += OnConnected;
            try
            {
                Assert.That(transport.Start(TimeoutMilliseconds), Is.True);
                transport.Connect("127.0.0.1", server.LocalPort, RealtimeProtocol.ConnectionKey);
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
    }
}
