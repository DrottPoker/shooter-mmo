using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class LiteNetLibRealtimeTransportTests
{
    [Fact]
    public void PacketPoolCoversOneBroadcastOfTheDefaultBudget()
    {
        // 1000 owner packets plus 38 MiB/s at 15 snapshots per second in 512-byte shares.
        Assert.Equal(6_188, LiteNetLibRealtimeTransport.PacketPoolSizeFor(1_000, 39_845_888 / 15));

        // 200 connections that all see each other queue 200 owner packets and 200 times nine
        // remote chunks, which the pool must hold.
        Assert.True(LiteNetLibRealtimeTransport.PacketPoolSizeFor(200, 39_845_888 / 15) >= 2_000);
    }

    [Fact]
    public void PacketPoolStaysWithinItsBounds()
    {
        Assert.Equal(1_000, LiteNetLibRealtimeTransport.PacketPoolSizeFor(1, 1_024));
        Assert.Equal(65_536, LiteNetLibRealtimeTransport.PacketPoolSizeFor(20_000, int.MaxValue));
    }

    // Pins the limit against the LiteNetLib in use: data of exactly the limit arrives whole over
    // loopback, and the worker's transport refuses one byte more, which LiteNetLib would turn
    // into zeros.
    [Fact]
    public async Task DisconnectDataOfTheLimitArrivesWholeAndMoreIsRefused()
    {
        await using var loopback = await LoopbackPeer.ConnectAsync("Limit Listener");
        var data = new byte[RealtimeDisconnectData.MaximumBytes];
        new Random(7).NextBytes(data);

        Assert.Throws<ArgumentException>(
            () => loopback.Transport.DisconnectPeer(loopback.Peer, new byte[RealtimeDisconnectData.MaximumBytes + 1]));
        loopback.Transport.DisconnectPeer(loopback.Peer, data);
        await loopback.PollUntilAsync(() => loopback.Client.IsDisconnected, "the disconnect");

        Assert.Equal(DisconnectReason.RemoteConnectionClose, loopback.Client.DisconnectReason);
        Assert.Equal(data, loopback.Client.DisconnectData);
    }

    // LiteNetLib's delivery event, not its outgoing queue, says that the client has the reason:
    // the queue is empty as soon as the packet left once.
    [Fact]
    public async Task TheClientsAcknowledgementMarksTheReasonAcknowledged()
    {
        await using var loopback = await LoopbackPeer.ConnectAsync("Acknowledging Listener");
        var disconnect = new RealtimePendingDisconnect(null, DateTime.UtcNow);

        loopback.Peer.SendDisconnectReason(RealtimeProtocol.EncodeLeaveAccepted(20), disconnect);
        await loopback.PollUntilAsync(() => disconnect.Acknowledged, "the acknowledgement");

        Assert.True(loopback.Client.Has(RealtimeMessageType.LeaveAccepted));
    }

    [Fact]
    public void AReasonThatFitsTravelsAsItIs()
    {
        var leave = RealtimeProtocol.EncodeLeaveAccepted(3_600);
        var disconnect = RealtimeProtocol.EncodeServerDisconnect("udp_rate_limited", "The realtime connection exceeded its inbound UDP quota.");

        Assert.Same(leave, RealtimeDisconnectData.Fit(leave));
        Assert.Same(disconnect, RealtimeDisconnectData.Fit(disconnect));
    }

    // A reason of more than 1,014 and at most the 1,200 bytes of a packet does not fit: the data
    // keeps the code whole and as much of the log message as fits, cut between whole characters.
    [Theory]
    [InlineData(0x20AC, false)]
    [InlineData(0x1F600, false)]
    [InlineData(0x20AC, true)]
    public void AReasonThatDoesNotFitKeepsItsCodeAndCutsItsMessage(int codePoint, bool joinRejected)
    {
        var character = char.ConvertFromUtf32(codePoint);
        // The protocol's longest code, 64 characters, and a message of about 1,100 UTF-8 bytes
        // within its 512 UTF-16 characters.
        var code = new string('c', 64);
        var bytesPerCharacter = Encoding.UTF8.GetByteCount(character);
        var message = string.Concat(Enumerable.Repeat(
            character,
            Math.Min(512 / character.Length, 1_100 / bytesPerCharacter)));
        var packet = joinRejected
            ? RealtimeProtocol.EncodeJoinRejected(code, message)
            : RealtimeProtocol.EncodeServerDisconnect(code, message);
        Assert.True(packet.Length > RealtimeDisconnectData.MaximumBytes);

        var data = RealtimeDisconnectData.Fit(packet);

        Assert.True(data.Length <= RealtimeDisconnectData.MaximumBytes);
        Assert.True(data.Length > RealtimeDisconnectData.MaximumBytes - bytesPerCharacter);
        RealtimeError fitted;
        string error;
        Assert.True(
            joinRejected
                ? RealtimeProtocol.TryDecodeJoinRejected(data, out fitted, out error)
                : RealtimeProtocol.TryDecodeServerDisconnect(data, out fitted, out error),
            error);
        Assert.Equal(code, fitted.Code);
        Assert.StartsWith(fitted.Message, message, StringComparison.Ordinal);
        Assert.Equal(0, fitted.Message.Length % character.Length);
    }

    [Fact]
    public void OnlyAMessageThatEndsTheSessionIsAReason()
    {
        Assert.Throws<ArgumentException>(
            () => RealtimeDisconnectData.Fit(RealtimeProtocol.EncodeLeaveRequest(Guid.NewGuid().ToString())));
        Assert.Throws<ArgumentException>(() => RealtimeDisconnectData.Fit(new byte[3]));
    }

    // The worker's own transport on a free loopback port with one recording client connected.
    private sealed class LoopbackPeer : IAsyncDisposable
    {
        private readonly RealtimeLoopFixture fixture;

        private LoopbackPeer(RealtimeLoopFixture fixture, LiteNetLibRealtimeTransport transport, RealtimeRecordingClient client)
        {
            this.fixture = fixture;
            Transport = transport;
            Client = client;
        }

        public LiteNetLibRealtimeTransport Transport { get; }

        public RealtimeRecordingClient Client { get; }

        public IRealtimePeerConnection Peer { get; private set; } = null!;

        public static async Task<LoopbackPeer> ConnectAsync(string name)
        {
            var fixture = new RealtimeLoopFixture();
            var config = RealtimeServerHarness.CreateConfig(FindAvailableUdpPort(), new RealtimeServerHarnessOptions());
            var lifecycle = new ConnectedPeers();
            var transport = new LiteNetLibRealtimeTransport(
                config,
                new RealtimeConnectionAdmission(config, null, new PeerAdmissionGuard(config.Admission), fixture.Metrics),
                fixture.LoopGuard,
                fixture.Logger);
            transport.Start(
                lifecycle,
                new RealtimeMessageDispatcher(fixture.Registry, fixture.Sender, fixture.Metrics, NullLogger.Instance, new RealtimeMessageRoutes()));
            var loopback = new LoopbackPeer(fixture, transport, new RealtimeRecordingClient(name, null));
            loopback.Client.Connect(config.UdpPort);
            await loopback.PollUntilAsync(
                () => loopback.Client.IsConnected && lifecycle.PeerIds.Count == 1,
                "the client to connect");
            Assert.True(transport.TryGetConnectedPeer(lifecycle.PeerIds[0], out var peer));
            loopback.Peer = peer;
            return loopback;
        }

        public async Task PollUntilAsync(Func<bool> condition, string description)
        {
            using var timeout = RealtimeServerHarness.CreateTimeout();
            while (!condition())
            {
                Transport.PollEvents();
                Transport.FlushSends();
                Client.PollEvents();
                try
                {
                    await Task.Delay(5, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out waiting for {description}.");
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            Transport.Stop();
            fixture.Dispose();
            return ValueTask.CompletedTask;
        }

        private static int FindAvailableUdpPort()
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        }
    }

    private sealed class ConnectedPeers : IRealtimePeerLifecycle
    {
        public List<int> PeerIds { get; } = [];

        public void OnPeerConnected(int peerId, IPAddress address)
        {
            PeerIds.Add(peerId);
        }

        public void OnPeerDisconnected(int peerId, DisconnectReason reason)
        {
        }
    }
}
