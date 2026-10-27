using LiteNetLib;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Compares the dispatcher with an oracle transcribed from the original
// HandleNetworkReceive, HandleUnauthenticatedMessage and HandleAuthenticatedMessage for
// every peer state, message type, channel and delivery method.
public sealed class RealtimeMessageDispatcherTests
{
    private static readonly string[] States =
    [
        "unauthenticated",
        "join_started",
        "authenticated",
        "leave_started",
        "disconnect_pending"
    ];

    private static readonly DeliveryMethod[] DeliveryMethods =
    [
        DeliveryMethod.Unreliable,
        DeliveryMethod.ReliableUnordered,
        DeliveryMethod.Sequenced,
        DeliveryMethod.ReliableOrdered,
        DeliveryMethod.ReliableSequenced
    ];

    private static readonly Dictionary<string, string> RejectionMessages = new(StringComparer.Ordinal)
    {
        ["invalid_packet"] = "Realtime packet header is invalid.",
        ["join_required"] = "A single join request must complete before other messages are accepted.",
        ["unexpected_message"] = "The message is not valid for the current connection state.",
        ["invalid_movement_delivery"] = "Movement inputs require the sequenced movement channel.",
        ["invalid_item_operation_delivery"] = "Item operation intents require the reliable ordered control channel.",
        ["invalid_corpse_operation_delivery"] = "Corpse interaction intents require the reliable ordered control channel.",
        ["invalid_world_interaction_delivery"] = "World interaction intents require the reliable ordered control channel."
    };

    [Fact]
    public void DispatchMatchesTheOriginalRoutingForEveryStateTypeAndDelivery()
    {
        var mismatches = new List<string>();
        foreach (var state in States)
        {
            for (var type = 0; type <= 30; type++)
            {
                for (byte channel = 0; channel <= 1; channel++)
                {
                    foreach (var delivery in DeliveryMethods)
                    {
                        var expected = Oracle(state, (byte)type, channel, delivery);
                        var actual = Dispatch(state, (byte)type, channel, delivery);
                        if (!string.Equals(expected, actual, StringComparison.Ordinal))
                        {
                            mismatches.Add(
                                $"{state} type {type} channel {channel} {delivery}: expected {expected}, got {actual}");
                        }
                    }
                }
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void TruncatedHeaderIsAnInvalidPacket()
    {
        var fixture = new DispatcherFixture();
        var (context, peer) = fixture.AddPeer("authenticated");

        fixture.Dispatcher.Dispatch(peer, [0x53, 0x4D], 0, DeliveryMethod.ReliableOrdered);

        Assert.Equal("reject:invalid_packet", fixture.Outcome(peer));
        Assert.NotNull(context.DisconnectAfterUtc);
    }

    [Fact]
    public void UnknownPeerIsDroppedWithoutCountingTheReceivedPacket()
    {
        var fixture = new DispatcherFixture();
        var peer = new RecordingPeerConnection(7);

        fixture.Dispatcher.Dispatch(
            peer,
            Header(RealtimeMessageType.JoinRequest),
            0,
            DeliveryMethod.ReliableOrdered);

        Assert.Equal("drop", fixture.Outcome(peer));
        Assert.Equal(0, fixture.Metrics.Capture().ReceivedPackets);
    }

    [Fact]
    public void PendingDisconnectCountsButDropsThePacket()
    {
        var fixture = new DispatcherFixture();
        var (_, peer) = fixture.AddPeer("disconnect_pending");

        fixture.Dispatcher.Dispatch(
            peer,
            Header(RealtimeMessageType.LeaveRequest),
            0,
            DeliveryMethod.ReliableOrdered);

        Assert.Equal("drop", fixture.Outcome(peer));
        Assert.Equal(1, fixture.Metrics.Capture().ReceivedPackets);
    }

    [Fact]
    public void InboundQuotaIsCheckedBeforeTheHeader()
    {
        var fixture = new DispatcherFixture(UdpQuotaConfig.Default with
        {
            InboundPacketsPerSecond = 1,
            InboundPacketBurst = 1
        });
        var (_, peer) = fixture.AddPeer("authenticated");
        fixture.Dispatcher.Dispatch(
            peer,
            Header(RealtimeMessageType.MovementInputBatch),
            1,
            DeliveryMethod.Sequenced);
        Assert.Equal("handle:movement", fixture.Outcome(peer));

        fixture.Dispatcher.Dispatch(peer, [0x00], 0, DeliveryMethod.ReliableOrdered);

        Assert.Equal("reject:udp_rate_limited", fixture.Outcome(peer));
        var metrics = fixture.Metrics.Capture();
        Assert.Equal(1, metrics.QuotaRejectedPackets);
        Assert.Equal(2, metrics.ReceivedPackets);
    }

    [Fact]
    public void HandlerFailureDisconnectsOnlyItsPeerWithAnInternalError()
    {
        var logger = new CapturingLogger<RealtimeMessageDispatcher>();
        var fixture = new DispatcherFixture(logger: logger, failingHandler: "leave");
        var (_, peer) = fixture.AddPeer("authenticated");

        fixture.Dispatcher.Dispatch(
            peer,
            Header(RealtimeMessageType.LeaveRequest),
            0,
            DeliveryMethod.ReliableOrdered);

        Assert.Equal("reject:internal_error", fixture.Outcome(peer));
        var entry = Assert.Single(logger.Entries, value => value.Level == LogLevel.Error);
        Assert.Equal(
            "[SIMULATION] Could not process a realtime packet from UDP peer {PeerId}.",
            entry.Template);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public void DuplicateRoutesAreRejected()
    {
        var fixture = new DispatcherFixture();
        var routes = RealtimeMessageRoutes.CreateUnauthenticated(Ignore);

        Assert.Throws<InvalidOperationException>(() => new RealtimeMessageDispatcher(
            fixture.Registry,
            fixture.Sender,
            fixture.Metrics,
            NullLogger.Instance,
            [.. routes, .. routes],
            []));
    }

    private static void Ignore(in RealtimeInboundMessage message)
    {
    }

    private static string Dispatch(
        string state,
        byte type,
        byte channel,
        DeliveryMethod delivery)
    {
        var fixture = new DispatcherFixture();
        var (_, peer) = fixture.AddPeer(state);
        var packet = RealtimeProtocol.EncodeLeaveAccepted();
        packet[6] = type;
        fixture.Dispatcher.Dispatch(peer, packet, channel, delivery);
        return fixture.Outcome(peer);
    }

    // Transcribed from the realtime service before the dispatcher existed.
    private static string Oracle(
        string state,
        byte type,
        byte channel,
        DeliveryMethod delivery)
    {
        if (state == "disconnect_pending")
        {
            return "drop";
        }

        if (!Enum.IsDefined(typeof(RealtimeMessageType), type))
        {
            return "reject:invalid_packet";
        }

        var messageType = (RealtimeMessageType)type;
        if (state is "unauthenticated" or "join_started")
        {
            if (messageType != RealtimeMessageType.JoinRequest
                || channel != RealtimeProtocol.ControlChannel
                || delivery != DeliveryMethod.ReliableOrdered
                || state == "join_started")
            {
                return "reject:join_required";
            }

            return "handle:join";
        }

        if (messageType == RealtimeMessageType.MovementInputBatch)
        {
            return channel != RealtimeProtocol.MovementInputChannel
                || delivery != DeliveryMethod.Sequenced
                ? "reject:invalid_movement_delivery"
                : "handle:movement";
        }

        if (messageType == RealtimeMessageType.ItemOperationIntent)
        {
            return channel != RealtimeProtocol.ControlChannel
                || delivery != DeliveryMethod.ReliableOrdered
                ? "reject:invalid_item_operation_delivery"
                : "handle:item";
        }

        if (messageType == RealtimeMessageType.CorpseInteractionIntent)
        {
            return channel != RealtimeProtocol.ControlChannel
                || delivery != DeliveryMethod.ReliableOrdered
                ? "reject:invalid_corpse_operation_delivery"
                : "handle:corpse";
        }

        if (messageType == RealtimeMessageType.WorldInteractionIntent)
        {
            return channel != RealtimeProtocol.ControlChannel
                || delivery != DeliveryMethod.ReliableOrdered
                ? "reject:invalid_world_interaction_delivery"
                : "handle:world";
        }

        if (messageType == RealtimeMessageType.CombatIntent)
        {
            return channel != RealtimeProtocol.ControlChannel
                || delivery != DeliveryMethod.ReliableOrdered
                ? "reject:invalid_combat_delivery"
                : "handle:combat";
        }

        if (messageType != RealtimeMessageType.LeaveRequest
            || channel != RealtimeProtocol.ControlChannel
            || delivery != DeliveryMethod.ReliableOrdered
            || state == "leave_started")
        {
            return "reject:unexpected_message";
        }

        return "handle:leave";
    }

    private static byte[] Header(RealtimeMessageType type)
    {
        var packet = RealtimeProtocol.EncodeLeaveAccepted();
        packet[6] = (byte)type;
        return packet;
    }

    private static ActiveSimulationSession CreateSession()
    {
        return new ActiveSimulationSession(
            Guid.NewGuid(),
            "dispatcher-session-token",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Dispatcher Hero",
            "local-shard-1",
            "development-world-1",
            "local-simulation-worker-1",
            "test-runtime",
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5),
            new PlayerCarryState(0, 0, 200),
            false);
    }

    private sealed class DispatcherFixture
    {
        private readonly RecordingPeerDirectory directory = new();
        private readonly UdpQuotaConfig quotas;
        private string? handled;

        public DispatcherFixture(
            UdpQuotaConfig? quotas = null,
            ILogger? logger = null,
            string? failingHandler = null)
        {
            this.quotas = quotas ?? UdpQuotaConfig.Default;
            Registry = new RealtimePeerRegistry(
                directory,
                new ConnectionEntityBindingRegistry(),
                new SimulationEntityRegistry(),
                Metrics);
            Sender = new RealtimePeerSender(Metrics, NullLogger.Instance);
            RealtimeMessageHandler Record(string name) => (in RealtimeInboundMessage message) =>
            {
                if (name == failingHandler)
                {
                    throw new InvalidOperationException("Handler failure.");
                }

                Assert.Same(directory.Peers[message.Context.PeerId], message.Peer);
                handled = name;
            };
            Dispatcher = new RealtimeMessageDispatcher(
                Registry,
                Sender,
                Metrics,
                logger ?? NullLogger.Instance,
                RealtimeMessageRoutes.CreateUnauthenticated(Record("join")),
                RealtimeMessageRoutes.CreateAuthenticated(
                    Record("movement"),
                    Record("item"),
                    Record("corpse"),
                    Record("world"),
                    Record("leave"),
                    Record("combat"),
                    Record("appearance"),
                    Record("admin")));
        }

        public RealtimeNetworkMetrics Metrics { get; } = new();

        public RealtimePeerRegistry Registry { get; }

        public RealtimePeerSender Sender { get; }

        public RealtimeMessageDispatcher Dispatcher { get; }

        public (RealtimePeerContext Context, RecordingPeerConnection Peer) AddPeer(string state)
        {
            var peer = new RecordingPeerConnection(3);
            directory.Peers[peer.Id] = peer;
            var context = new RealtimePeerContext(peer.Id, DateTime.UtcNow, quotas);
            context.JoinStarted = state == "join_started";
            if (state is "authenticated" or "leave_started" or "disconnect_pending")
            {
                context.Session = CreateSession();
            }

            context.LeaveStarted = state == "leave_started";
            if (state == "disconnect_pending")
            {
                context.ScheduleDisconnect(DateTime.UtcNow);
            }

            Registry.Register(context);
            return (context, peer);
        }

        public string Outcome(RecordingPeerConnection peer)
        {
            if (handled is not null)
            {
                var result = $"handle:{handled}";
                handled = null;
                Assert.Empty(peer.Sent);
                return result;
            }

            if (peer.Sent.Count == 0)
            {
                return "drop";
            }

            var sent = Assert.Single(peer.Sent);
            peer.Sent.Clear();
            Assert.Equal(DeliveryMethod.ReliableOrdered, sent.Delivery);
            Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(
                sent.Data,
                out var rejection,
                out var error), error);
            if (RejectionMessages.TryGetValue(rejection.Code, out var message))
            {
                Assert.Equal(message, rejection.Message);
            }

            return $"reject:{rejection.Code}";
        }
    }
}
