using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.SharedTests;
using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.Tests;

public sealed class SimulationBotSessionTests
{
    private const long StartTimestamp = 1_000_000;
    private const long JoinedTimestamp = StartTimestamp + 1;
    private static readonly TimeSpan JoinAndLeaveTimeout = TimeSpan.FromSeconds(10);
    private static readonly SimulationBotIdentity Identity = new(
        1,
        Guid.Parse("8d1f4c2e-58f4-4a52-a0b3-5f0c6d1e2a01"),
        Guid.Parse("3c0b9a7d-1e2f-4b6a-9d8c-7e6f5a4b3c02"),
        "Active Bot 1",
        "ticket-1",
        "local-shard-2",
        "development-world-1");

    private static readonly IReadOnlyDictionary<string, byte[]> Vectors =
        RealtimeProtocolGolden.EncodeVectors();

    // Messages that start or end the session; the join and leave tests cover them.
    private static readonly HashSet<RealtimeMessageType> SessionFlowMessages =
    [
        RealtimeMessageType.JoinAccepted,
        RealtimeMessageType.JoinRejected,
        RealtimeMessageType.LeaveAccepted,
        RealtimeMessageType.LeaveRejected,
        RealtimeMessageType.ServerDisconnect
    ];

    [Fact]
    public void AcceptsEveryMessageTheWorkerSendsToAJoinedSession()
    {
        AssertAcceptsEverySessionMessage(leaving: false);
    }

    [Fact]
    public void AcceptsEveryMessageTheWorkerSendsWhileTheLeaveIsPending()
    {
        AssertAcceptsEverySessionMessage(leaving: true);
    }

    // The worker sends every player its own look when it joins.
    [Fact]
    public void StaysJoinedWhenTheWorkerSendsTheBotItsOwnLook()
    {
        var bot = JoinedBot();

        bot.Receive(RealtimeProtocol.EncodeEntityAppearanceChanged(new RealtimeEntityAppearanceChanged(
            JoinAcceptance().ControlledEntityId,
            new RealtimeCharacterAppearance("Human_Male", [], [], false))));

        AssertState(bot.Session, SimulationBotClientState.Joined);
    }

    [Fact]
    public void JoinsWithItsTicketOnTheControlChannel()
    {
        var transport = new RecordingTransport();
        var session = NewSession();
        session.Start(StartTimestamp);

        session.OnConnected(transport);

        Assert.Equal(SimulationBotClientState.Joining, session.State);
        var sent = Assert.Single(transport.Sent);
        Assert.Equal(RealtimeProtocol.ControlChannel, sent.Channel);
        Assert.Equal(RealtimeDelivery.ReliableOrdered, sent.Delivery);
        Assert.True(RealtimeProtocol.TryDecodeJoinRequest(sent.Packet, out var ticket, out _));
        Assert.Equal(Identity.Ticket, ticket);
    }

    [Fact]
    public void FailsWhenTheAcceptedCharacterIsNotTheIssuedOne()
    {
        var session = ConnectedSession(out _);
        var accepted = JoinAcceptance(characterId: Guid.NewGuid());

        session.Receive(
            RealtimeProtocol.EncodeJoinAccepted(accepted),
            RealtimeProtocol.ControlChannel,
            RealtimeDelivery.ReliableOrdered,
            JoinedTimestamp);

        AssertFailed(session, "join_identity_mismatch");
    }

    [Fact]
    public void FailsWhenTheWorkerDoesNotAcceptTheJoinInTime()
    {
        var session = ConnectedSession(out _);
        var deadline = StartTimestamp + Ticks(JoinAndLeaveTimeout);

        session.Update(deadline - 1);
        Assert.Equal(SimulationBotClientState.Joining, session.State);
        session.Update(deadline);

        AssertFailed(session, "join_timeout");
    }

    [Fact]
    public void IgnoresAConnectionThatCompletesAfterTheJoinTimedOut()
    {
        var transport = new RecordingTransport();
        var session = NewSession();
        session.Start(StartTimestamp);
        session.Update(StartTimestamp + Ticks(JoinAndLeaveTimeout));

        session.OnConnected(transport);

        AssertFailed(session, "join_timeout");
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void DropsASnapshotThatOvertakesTheJoinAcceptance()
    {
        var session = ConnectedSession(out _);

        session.Receive(
            VectorOf(RealtimeMessageType.OwnerSimulationSnapshot),
            RealtimeProtocol.UnreliableReceiveChannel,
            RealtimeDelivery.Unreliable,
            JoinedTimestamp);

        Assert.Equal(SimulationBotClientState.Joining, session.State);
        Assert.Equal(0, session.Capture().SnapshotsReceived);
    }

    [Fact]
    public void FailsOnAReliableSessionMessageBeforeTheJoinAcceptance()
    {
        var session = ConnectedSession(out _);

        session.Receive(
            VectorOf(RealtimeMessageType.EntitySpawn),
            RealtimeProtocol.ControlChannel,
            RealtimeDelivery.ReliableOrdered,
            JoinedTimestamp);

        AssertFailed(session, "unexpected_message");
    }

    [Fact]
    public void FailsOnAMessageWithAnotherDeliveryThanTheProtocolNames()
    {
        var bot = JoinedBot();

        bot.Session.Receive(
            VectorOf(RealtimeMessageType.EntityAppearanceChanged),
            RealtimeProtocol.UnreliableReceiveChannel,
            RealtimeDelivery.Unreliable,
            JoinedTimestamp);

        AssertFailed(bot.Session, "invalid_delivery");
    }

    [Fact]
    public void FailsOnAMessageThatOnlyClientsSend()
    {
        var bot = JoinedBot();

        bot.Session.Receive(
            VectorOf(RealtimeMessageType.MovementInputBatch),
            RealtimeProtocol.MovementInputChannel,
            RealtimeDelivery.Sequenced,
            JoinedTimestamp);

        AssertFailed(bot.Session, "unexpected_message");
    }

    [Fact]
    public void KeepsTheFirstFailureReason()
    {
        var session = ConnectedSession(out _);
        session.Receive(
            VectorOf(RealtimeMessageType.JoinRejected),
            RealtimeProtocol.ControlChannel,
            RealtimeDelivery.ReliableOrdered,
            JoinedTimestamp);
        var firstCode = session.Capture().FailureCode;

        session.Receive(
            VectorOf(RealtimeMessageType.ServerDisconnect),
            RealtimeProtocol.ControlChannel,
            RealtimeDelivery.ReliableOrdered,
            JoinedTimestamp);
        session.OnDisconnected("RemoteConnectionClose");

        Assert.Equal(SimulationBotClientState.Failed, session.State);
        Assert.NotNull(firstCode);
        Assert.Equal(firstCode, session.Capture().FailureCode);
    }

    [Fact]
    public void SendsOneInputPerTickAndCarriesThePathUntilAnOwnerSnapshotConfirmsIt()
    {
        var path = StraightPath(7);
        var latencies = new List<double>();
        var bot = JoinedBot(new RecordingInputSource(_ => path), latencies.Add);
        var interval = TickInterval();

        bot.Session.Update(JoinedTimestamp);
        bot.Session.Update(JoinedTimestamp + interval);

        var batches = bot.Transport.Sent.Select(DecodeBatch).ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal([1u], batches[0].Inputs.Select(input => input.InputSequence));
        Assert.Equal([1u, 2u], batches[1].Inputs.Select(input => input.InputSequence));
        Assert.All(batches, batch => Assert.Equal(7u, batch.Path?.PathId));
        Assert.All(bot.Transport.Sent, sent =>
        {
            Assert.Equal(RealtimeProtocol.MovementInputChannel, sent.Channel);
            Assert.Equal(RealtimeDelivery.Sequenced, sent.Delivery);
        });

        bot.ReceiveSnapshot(
            OwnerSnapshot(sequence: 1, lastProcessedInput: 2, pathId: 7),
            JoinedTimestamp + interval + 5);
        Assert.Equal(2, latencies.Count);
        bot.Session.Update(JoinedTimestamp + (2 * interval));

        var confirmed = DecodeBatch(bot.Transport.Sent[^1]);
        Assert.Equal([3u], confirmed.Inputs.Select(input => input.InputSequence));
        Assert.Equal(7u, confirmed.Inputs[0].PathId);
        Assert.Null(confirmed.Path);
    }

    [Fact]
    public void AnOlderOwnerSnapshotDoesNotMoveTheBotBackOrUnconfirmItsPath()
    {
        var positions = new List<SimulationBotPosition>();
        var path = StraightPath(7);
        var bot = JoinedBot(new RecordingInputSource(position =>
        {
            positions.Add(position);
            return path;
        }));

        bot.ReceiveSnapshot(OwnerSnapshot(sequence: 5, lastProcessedInput: 0, pathId: 7, x: 3f), JoinedTimestamp);
        bot.ReceiveSnapshot(OwnerSnapshot(sequence: 4, lastProcessedInput: 0, pathId: 0, x: 1f), JoinedTimestamp);
        bot.Session.Update(JoinedTimestamp);

        Assert.Equal(3f, Assert.Single(positions).X);
        Assert.Null(DecodeBatch(Assert.Single(bot.Transport.Sent)).Path);
        Assert.Equal(5u, bot.Session.Capture().LatestServerTick);
    }

    [Fact]
    public void SendsAtMostThreeInputsInOneUpdateAndSkipsTheMissedOnes()
    {
        var bot = JoinedBot();
        var interval = TickInterval();
        var late = JoinedTimestamp + (10 * interval);

        bot.Session.Update(late);
        Assert.Equal(3, bot.Transport.Sent.Count);
        bot.Session.Update(late);
        Assert.Equal(3, bot.Transport.Sent.Count);
        bot.Session.Update(late + interval);

        Assert.Equal(4, bot.Transport.Sent.Count);
        Assert.Equal(4u, DecodeBatch(bot.Transport.Sent[^1]).Inputs[^1].InputSequence);
    }

    [Fact]
    public void LeavesCleanlyWhenTheWorkerAcceptsTheLeave()
    {
        var bot = JoinedBot();

        bot.Session.BeginLeave(JoinedTimestamp);
        bot.Session.Update(JoinedTimestamp + TickInterval());

        var sent = Assert.Single(bot.Transport.Sent);
        Assert.Equal(RealtimeProtocol.ControlChannel, sent.Channel);
        Assert.Equal(RealtimeDelivery.ReliableOrdered, sent.Delivery);
        Assert.True(RealtimeProtocol.TryDecodeLeaveRequest(sent.Packet, out var sessionId, out _));
        Assert.Equal(JoinAcceptance().SimulationSessionId, sessionId);
        Assert.False(bot.Session.SendItemOperation(RealtimeItemOperationIntent.CreateRelocate(
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            1,
            Guid.NewGuid())));

        bot.Receive(VectorOf(RealtimeMessageType.LeaveAccepted));
        bot.Session.OnDisconnected("RemoteConnectionClose");

        AssertState(bot.Session, SimulationBotClientState.Completed);
    }

    [Fact]
    public void FailsWhenTheWorkerDoesNotAcceptTheLeaveInTime()
    {
        var bot = JoinedBot();
        var leaveTimestamp = JoinedTimestamp + 100;
        bot.Session.BeginLeave(leaveTimestamp);

        bot.Session.Update(leaveTimestamp + Ticks(JoinAndLeaveTimeout));

        AssertFailed(bot.Session, "leave_timeout");
    }

    [Fact]
    public void FailsOnALeaveAcceptanceWithoutAPendingLeave()
    {
        var bot = JoinedBot();

        bot.Receive(VectorOf(RealtimeMessageType.LeaveAccepted));

        AssertFailed(bot.Session, "invalid_leave_acceptance");
    }

    private static void AssertAcceptsEverySessionMessage(bool leaving)
    {
        var failures = new List<string>();
        var covered = new HashSet<RealtimeMessageType>();
        foreach (var (name, packet) in Vectors.OrderBy(vector => vector.Key, StringComparer.Ordinal))
        {
            Assert.True(RealtimeProtocol.TryReadMessageType(packet, out var type), name);
            Assert.True(RealtimeProtocol.TryGetDescriptor(type, out var descriptor), name);
            if (descriptor.Direction != RealtimeMessageDirection.ServerToClient
                || SessionFlowMessages.Contains(type))
            {
                continue;
            }

            covered.Add(type);
            var bot = JoinedBot();
            if (leaving)
            {
                bot.Session.BeginLeave(JoinedTimestamp);
            }

            bot.Session.Receive(packet, descriptor.Channel, descriptor.Delivery, JoinedTimestamp);
            var expected = leaving ? SimulationBotClientState.Leaving : SimulationBotClientState.Joined;
            var snapshot = bot.Session.Capture();
            if (snapshot.State != expected)
            {
                failures.Add($"{name}: {snapshot.State}, {snapshot.FailureCode}: {snapshot.FailureMessage}");
            }
        }

        // A message type without a vector would not be tested at all.
        var serverMessages = Enum.GetValues<RealtimeMessageType>()
            .Where(type => RealtimeProtocol.TryGetDescriptor(type, out var descriptor)
                && descriptor.Direction == RealtimeMessageDirection.ServerToClient
                && !SessionFlowMessages.Contains(type))
            .Order()
            .ToArray();
        Assert.Equal(serverMessages, covered.Order().ToArray());
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static SimulationBotSession NewSession(
        ISimulationBotInputSource? inputSource = null,
        Action<double>? inputAcknowledged = null)
    {
        return new SimulationBotSession(
            Identity,
            JoinAndLeaveTimeout,
            inputSource ?? new RecordingInputSource(_ => null),
            inputAcknowledged);
    }

    private static SimulationBotSession ConnectedSession(out RecordingTransport transport)
    {
        transport = new RecordingTransport();
        var session = NewSession();
        session.Start(StartTimestamp);
        session.OnConnected(transport);
        return session;
    }

    private static JoinedBotSession JoinedBot(
        ISimulationBotInputSource? inputSource = null,
        Action<double>? inputAcknowledged = null)
    {
        var transport = new RecordingTransport();
        var session = NewSession(inputSource, inputAcknowledged);
        session.Start(StartTimestamp);
        session.OnConnected(transport);
        session.Receive(
            RealtimeProtocol.EncodeJoinAccepted(JoinAcceptance()),
            RealtimeProtocol.ControlChannel,
            RealtimeDelivery.ReliableOrdered,
            JoinedTimestamp);
        AssertState(session, SimulationBotClientState.Joined);
        transport.Sent.Clear();
        return new JoinedBotSession(session, transport);
    }

    // The protocol's typical join acceptance, issued to this bot's identity.
    private static RealtimeJoinAccepted JoinAcceptance(Guid? characterId = null)
    {
        Assert.True(RealtimeProtocol.TryDecodeJoinAccepted(
            Vectors["Session/JoinAccepted/typical"],
            out var template,
            out var error), error);
        return new RealtimeJoinAccepted(
            template.SimulationSessionId,
            Identity.AccountId.ToString(),
            (characterId ?? Identity.CharacterId).ToString(),
            Identity.CharacterName,
            Identity.ShardId,
            Identity.WorldId,
            template.ControlledEntityId,
            template.SimulationRevision,
            template.CollisionRevision,
            template.JoinedAt,
            template.SessionExpiresAt,
            isReconnect: false,
            template.CarryState,
            template.MovementSettings,
            template.InitialPlayerState,
            template.ShardRules);
    }

    private static byte[] OwnerSnapshot(
        uint sequence,
        uint lastProcessedInput,
        uint pathId,
        float x = 0f)
    {
        return RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
            sequence,
            sequence,
            lastProcessedInput,
            new RealtimePlayerState(x, 0f, 0f, 0f, 0f, 0f, 0f, true),
            new RealtimeMovementPathProgress(pathId, 0, 0)));
    }

    private static RealtimeMovementPath StraightPath(uint pathId)
    {
        return new RealtimeMovementPath(pathId, [new RealtimeMovementPathWaypoint(4f, 0f, 4f)]);
    }

    private static byte[] VectorOf(RealtimeMessageType type)
    {
        return Vectors
            .OrderBy(vector => vector.Key, StringComparer.Ordinal)
            .First(vector => RealtimeProtocol.TryReadMessageType(vector.Value, out var vectorType)
                && vectorType == type)
            .Value;
    }

    private static RealtimeMovementInputBatch DecodeBatch(SentPacket sent)
    {
        Assert.True(RealtimeProtocol.TryDecodeMovementInputBatch(sent.Packet, out var batch, out var error), error);
        return batch;
    }

    private static long TickInterval()
    {
        return Ticks(TimeSpan.FromSeconds(1d / JoinAcceptance().MovementSettings.TickRateHz));
    }

    private static long Ticks(TimeSpan duration)
    {
        return (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
    }

    private static void AssertState(SimulationBotSession session, SimulationBotClientState expected)
    {
        var snapshot = session.Capture();
        Assert.True(
            snapshot.State == expected,
            $"Expected {expected}, was {snapshot.State}: {snapshot.FailureCode}: {snapshot.FailureMessage}");
    }

    private static void AssertFailed(SimulationBotSession session, string code)
    {
        Assert.Equal(SimulationBotClientState.Failed, session.State);
        Assert.Equal(code, session.Capture().FailureCode);
    }

    private sealed record JoinedBotSession(SimulationBotSession Session, RecordingTransport Transport)
    {
        public void Receive(byte[] packet)
        {
            Session.Receive(packet, RealtimeProtocol.ControlChannel, RealtimeDelivery.ReliableOrdered, JoinedTimestamp);
        }

        public void ReceiveSnapshot(byte[] packet, long timestamp)
        {
            Session.Receive(packet, RealtimeProtocol.UnreliableReceiveChannel, RealtimeDelivery.Unreliable, timestamp);
        }
    }

    private sealed record SentPacket(byte[] Packet, byte Channel, RealtimeDelivery Delivery);

    private sealed class RecordingTransport : ISimulationBotTransport
    {
        public List<SentPacket> Sent { get; } = [];

        public void Send(byte[] packet, byte channel, RealtimeDelivery delivery)
        {
            Sent.Add(new SentPacket(packet, channel, delivery));
        }
    }

    private sealed class RecordingInputSource(
        Func<SimulationBotPosition, RealtimeMovementPath?> selectPath) : ISimulationBotInputSource
    {
        public RealtimeMovementPath? SelectPath(
            uint clientTick,
            RealtimeJoinAccepted joinedSession,
            SimulationBotPosition position)
        {
            return selectPath(position);
        }
    }
}
