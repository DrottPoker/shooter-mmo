using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Pins how the worker ends a connection: the reliable message that says why goes first, the
// disconnect follows once the client acknowledged it or at the configured maximum, no sooner than
// the next supervision, and it carries the message as its data. The recording peers acknowledge
// on demand; the tests over real UDP lose the reliable copy through a relay.
public sealed class RealtimeGracefulDisconnectTests
{
    [Fact]
    public void TheDisconnectWaitsForTheAcknowledgementAndCarriesTheReason()
    {
        using var fixture = new RealtimeLoopFixture();
        var supervisor = Supervisor(fixture, TimeSpan.FromHours(1));
        var player = fixture.ConnectPlayer(1);

        fixture.Sender.RejectProtocol(player.Peer, player.Context, "invalid_packet", "Realtime packet header is invalid.");
        for (var iteration = 0; iteration < 3; iteration++)
        {
            supervisor.EnforcePeerState();
        }

        Assert.Empty(fixture.Directory.Disconnected);
        var reason = Assert.Single(player.Peer.Sent);
        Assert.Equal(RealtimeMessageType.ServerDisconnect, reason.Type);
        Assert.Equal(LiteNetLib.DeliveryMethod.ReliableOrdered, reason.Delivery);

        player.Peer.AcknowledgeReasons();
        supervisor.EnforcePeerState();

        var disconnect = Assert.Single(fixture.Directory.Disconnected);
        Assert.Equal(1, disconnect.PeerId);
        Assert.Equal(reason.Data, disconnect.Data);
    }

    [Fact]
    public async Task WithoutAnAcknowledgementTheDisconnectGoesAtTheMaximum()
    {
        using var fixture = new RealtimeLoopFixture();
        var maximum = TimeSpan.FromMilliseconds(50);
        var supervisor = Supervisor(fixture, maximum);
        var player = fixture.ConnectPlayer(1);

        fixture.Sender.SendAndDisconnect(player.Peer, player.Context, RealtimeProtocol.EncodeLeaveAccepted(20));
        supervisor.EnforcePeerState();
        Assert.Empty(fixture.Directory.Disconnected);
        await Task.Delay(maximum + TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        supervisor.EnforcePeerState();

        var disconnect = Assert.Single(fixture.Directory.Disconnected);
        Assert.Equal(RealtimeProtocol.EncodeLeaveAccepted(20), disconnect.Data);
        Assert.True(DateTime.UtcNow - player.Context.PendingDisconnect!.RequestedAtUtc >= maximum);
    }

    // The reason goes out at the end of the loop iteration that decided the disconnect, so even
    // an acknowledgement that the test fakes at once waits for the next supervision.
    [Fact]
    public void TheDisconnectGoesNoSoonerThanTheNextSupervision()
    {
        using var fixture = new RealtimeLoopFixture();
        var supervisor = Supervisor(fixture, TimeSpan.FromHours(1));
        var player = fixture.ConnectPlayer(1);

        fixture.Sender.SendAndDisconnect(
            player.Peer,
            player.Context,
            RealtimeProtocol.EncodeServerDisconnect("session_revoked", "The simulation session is no longer active."));
        player.Peer.AcknowledgeReasons();
        supervisor.EnforcePeerState();
        Assert.Empty(fixture.Directory.Disconnected);

        supervisor.EnforcePeerState();
        Assert.Single(fixture.Directory.Disconnected);
    }

    // A client takes the first message that ends its session, so a peer that already goes keeps
    // that reason, and the worker sends no second one.
    [Fact]
    public void APeerThatIsAlreadyGoingKeepsItsFirstReason()
    {
        using var fixture = new RealtimeLoopFixture();
        var supervisor = Supervisor(fixture, TimeSpan.FromHours(1));
        var player = fixture.ConnectPlayer(1);
        var leave = RealtimeProtocol.EncodeLeaveAccepted(20);

        fixture.Sender.SendAndDisconnect(player.Peer, player.Context, leave);
        fixture.Sender.RejectProtocol(player.Peer, player.Context, "udp_rate_limited", "The realtime connection exceeded its inbound UDP quota.");
        player.Peer.AcknowledgeReasons();
        supervisor.EnforcePeerState();
        supervisor.EnforcePeerState();

        Assert.Equal(leave, Assert.Single(player.Peer.Sent).Data);
        Assert.Equal(leave, Assert.Single(fixture.Directory.Disconnected).Data);
    }

    // A reason that could not be sent is never acknowledged, so the peer goes at the next
    // supervision, and the disconnect still carries the reason.
    [Fact]
    public void AReasonThatCouldNotBeSentGoesWithTheDisconnectAtOnce()
    {
        using var fixture = new RealtimeLoopFixture();
        var supervisor = Supervisor(fixture, TimeSpan.FromHours(1));
        var player = fixture.ConnectPlayer(1);
        player.Peer.FailReliableSends = true;
        var failuresBefore = fixture.LoopGuard.TotalFailures;

        fixture.Sender.RejectProtocol(player.Peer, player.Context, "invalid_packet", "Realtime packet header is invalid.");
        supervisor.EnforcePeerState();
        supervisor.EnforcePeerState();

        Assert.Empty(player.Peer.Sent);
        Assert.Equal(failuresBefore + 1, fixture.LoopGuard.TotalFailures);
        var disconnect = Assert.Single(fixture.Directory.Disconnected);
        Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(disconnect.Data!, out var reason, out _));
        Assert.Equal("invalid_packet", reason.Code);
    }

    // A client that never acknowledges grows LiteNetLib's outgoing queue; beyond the limit the
    // peer goes at once and without a reason, as no reliable message would arrive anyway.
    [Fact]
    public void APeerBeyondTheReliableBacklogGoesAtOnceWithoutData()
    {
        using var fixture = new RealtimeLoopFixture();
        var supervisor = Supervisor(fixture, TimeSpan.FromHours(1));
        var player = fixture.ConnectPlayer(1);
        player.Peer.QueuedReliablePackets = fixture.Config.Admission.MaxReliableQueuedPackets + 1;

        supervisor.EnforcePeerState();

        var disconnect = Assert.Single(fixture.Directory.Disconnected);
        Assert.Null(disconnect.Data);
        Assert.Empty(player.Peer.Sent);
    }

    [Fact]
    public async Task ALeaveIsDisconnectedOnceTheClientAcknowledgedTheAnswer()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Quick Leaver");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.SendLeave(client.JoinAccepted!.SimulationSessionId);
        await harness.PollUntilAsync(() => client.IsDisconnected, "the disconnect after the leave");

        var answer = Assert.Single(client.Packets, packet => packet.Type == RealtimeMessageType.LeaveAccepted);
        Assert.Equal(answer.Data, client.DisconnectData);
        Assert.Equal(LiteNetLib.DisconnectReason.RemoteConnectionClose, client.DisconnectReason);
        // An acknowledgement over loopback takes milliseconds; the maximum is two seconds.
        Assert.True(
            client.DisconnectedAtUtc!.Value - answer.ReceivedAtUtc < harness.Config.GracefulDisconnectMaximum / 2,
            "The disconnect must follow the acknowledgement, not the maximum.");
    }

    [Fact]
    public async Task ALeaveWhoseAnswerTheLineLostEndsWithTheAnswerInTheDisconnect()
    {
        var maximum = TimeSpan.FromMilliseconds(400);
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            GracefulDisconnectMaximum = maximum
        });
        var character = harness.AddCharacter("Lossy Leaver");
        await harness.StartAsync();
        using var relay = new LossyUdpRelay(harness.Port);
        var client = await harness.JoinAsync(character, viaPort: relay.Port);
        relay.LoseReliableToClient();

        var leaveSentAt = DateTime.UtcNow;
        client.SendLeave(client.JoinAccepted!.SimulationSessionId);
        await harness.PollUntilAsync(() => client.IsDisconnected, "the disconnect after the lost answer");

        Assert.False(client.Has(RealtimeMessageType.LeaveAccepted));
        Assert.True(relay.LostToClient > 0);
        Assert.True(RealtimeProtocol.TryDecodeLeaveAccepted(client.DisconnectData!, out var bodySeconds, out var error), error);
        Assert.Equal(SimulationWorkerConfig.DefaultLingeringBodySeconds, bodySeconds);
        Assert.True(client.DisconnectedAtUtc!.Value - leaveSentAt >= maximum);
    }

    // The reliable stop notice stays, and every peer also gets it as the data of its disconnect,
    // so a peer whose notice the line lost still learns why its worker went away.
    [Fact]
    public async Task AStoppingWorkerTellsAPeerThatLostTheNoticeWithTheDisconnect()
    {
        await using var harness = RealtimeServerHarness.Create();
        var lossy = harness.AddCharacter("Lossy Listener");
        var steady = harness.AddCharacter("Steady Listener");
        await harness.StartAsync();
        using var relay = new LossyUdpRelay(harness.Port);
        var lossyClient = await harness.JoinAsync(lossy, viaPort: relay.Port);
        var steadyClient = await harness.JoinAsync(steady);
        relay.LoseReliableToClient();

        await harness.StopWhilePollingAsync();
        await harness.PollUntilAsync(
            () => lossyClient.IsDisconnected && steadyClient.IsDisconnected,
            "both clients to be disconnected");

        Assert.Null(lossyClient.ServerDisconnect);
        Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(lossyClient.DisconnectData!, out var lostNotice, out _));
        Assert.Equal("server_shutdown", lostNotice.Code);
        Assert.Equal("server_shutdown", steadyClient.ServerDisconnect!.Code);
        Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(steadyClient.DisconnectData!, out var notice, out _));
        Assert.Equal(steadyClient.ServerDisconnect.Code, notice.Code);
    }

    private static RealtimePeerSupervisor Supervisor(RealtimeLoopFixture fixture, TimeSpan maximum)
    {
        return new RealtimePeerSupervisor(
            fixture.Config with { GracefulDisconnectMaximum = maximum },
            fixture.Registry,
            fixture.Directory,
            fixture.Sender,
            fixture.SessionStore,
            fixture.Teardown,
            fixture.Bodies,
            fixture.SessionOperations,
            fixture.Metrics,
            NullLogger.Instance);
    }
}
