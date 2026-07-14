using System.Diagnostics;
using System.Net;
using LiteNetLib;
using ShooterMmo.GameProtocol;

namespace ShooterMmo.Tools.SimulationStressGenerator;

public enum StressBotState
{
    Created,
    Connecting,
    Joining,
    Joined,
    Leaving,
    Completed,
    Failed
}

public sealed class StressBot : IDisposable
{
    private const int MaximumTrackedUnacknowledgedInputs = 256;

    private readonly StressGeneratorOptions options;
    private readonly StressIssuedTicket identity;
    private readonly EventBasedNetListener listener = new();
    private readonly NetManager client;
    private readonly List<PendingInput> pendingInputs = [];
    private readonly StressLatencyAccumulator inputAcknowledgementLatencies;
    private readonly StressLatencyAccumulator intervalInputAcknowledgementLatencies;
    private NetPeer? serverPeer;
    private RealtimeJoinAccepted? joinedSession;
    private long startedTimestamp;
    private long joinDeadlineTimestamp;
    private long lastPollTimestamp;
    private long nextInputTimestamp;
    private long leaveDeadlineTimestamp;
    private uint nextInputSequence;
    private uint clientTick;
    private uint lastSnapshotSequence;
    private bool hasSnapshotSequence;
    private bool disposed;
    private string? failureCode;
    private string? failureMessage;
    private double? joinLatencyMs;
    private long packetsSent;
    private long bytesSent;
    private long packetsReceived;
    private long bytesReceived;
    private long snapshotsReceived;
    private long estimatedMissingSnapshots;
    private long spawnPackets;
    private long despawnPackets;
    private long unacknowledgedInputsDropped;
    private uint latestServerTick;

    public StressBot(
        StressGeneratorOptions options,
        StressIssuedTicket identity,
        StressLatencyAccumulator inputAcknowledgementLatencies,
        StressLatencyAccumulator intervalInputAcknowledgementLatencies)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.inputAcknowledgementLatencies = inputAcknowledgementLatencies
            ?? throw new ArgumentNullException(nameof(inputAcknowledgementLatencies));
        this.intervalInputAcknowledgementLatencies = intervalInputAcknowledgementLatencies
            ?? throw new ArgumentNullException(nameof(intervalInputAcknowledgementLatencies));
        client = new NetManager(listener)
        {
            ChannelsCount = RealtimeProtocol.ChannelCount,
            DisconnectTimeout = (int)options.JoinTimeout.TotalMilliseconds,
            MaxPacketPerManualReceive = 256
        };

        listener.PeerConnectedEvent += HandleConnected;
        listener.PeerDisconnectedEvent += HandleDisconnected;
        listener.NetworkReceiveEvent += HandleNetworkReceive;
    }

    public StressBotState State { get; private set; } = StressBotState.Created;

    public void Start()
    {
        ThrowIfDisposed();
        if (State != StressBotState.Created)
        {
            throw new InvalidOperationException("Stress bot can only be started once.");
        }

        startedTimestamp = Stopwatch.GetTimestamp();
        lastPollTimestamp = startedTimestamp;
        joinDeadlineTimestamp = AddDuration(startedTimestamp, options.JoinTimeout);
        if (!client.Start(IPAddress.Any, IPAddress.IPv6Any, 0, manualMode: true))
        {
            Fail("client_start_failed", "LiteNetLib could not start the stress bot transport.");
            return;
        }

        State = StressBotState.Connecting;
        serverPeer = client.Connect(
            options.WorkerHost,
            options.WorkerUdpPort,
            RealtimeProtocol.ConnectionKey);
        if (serverPeer is null)
        {
            Fail("connection_start_failed", "LiteNetLib could not create the stress bot connection.");
        }
    }

    public void Poll(long nowTimestamp)
    {
        ThrowIfDisposed();
        client.PollEvents();
        client.ManualUpdate(
            (float)Math.Max(
                0d,
                Stopwatch.GetElapsedTime(lastPollTimestamp, nowTimestamp).TotalMilliseconds));
        lastPollTimestamp = nowTimestamp;

        if (State is StressBotState.Connecting or StressBotState.Joining
            && nowTimestamp >= joinDeadlineTimestamp)
        {
            Fail("join_timeout", "SimulationWorker did not accept the stress bot before the join timeout.");
            return;
        }

        if (State == StressBotState.Joined)
        {
            SendDueInputs(nowTimestamp);
        }
        else if (State == StressBotState.Leaving
            && nowTimestamp >= leaveDeadlineTimestamp)
        {
            Fail("leave_timeout", "SimulationWorker did not acknowledge the stress bot leave request.");
        }
    }

    public void BeginLeave(long nowTimestamp)
    {
        ThrowIfDisposed();
        if (State != StressBotState.Joined
            || serverPeer is null
            || joinedSession is null)
        {
            return;
        }

        var packet = RealtimeProtocol.EncodeLeaveRequest(joinedSession.SimulationSessionId);
        serverPeer.Send(packet, DeliveryMethod.ReliableOrdered);
        RecordSent(packet.Length);
        State = StressBotState.Leaving;
        leaveDeadlineTimestamp = AddDuration(nowTimestamp, options.JoinTimeout);
    }

    public StressBotSnapshot Capture()
    {
        return new StressBotSnapshot(
            identity.BotIndex,
            identity.CharacterName,
            State,
            joinLatencyMs,
            packetsSent,
            bytesSent,
            packetsReceived,
            bytesReceived,
            snapshotsReceived,
            estimatedMissingSnapshots,
            spawnPackets,
            despawnPackets,
            unacknowledgedInputsDropped,
            latestServerTick,
            failureCode,
            failureMessage);
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
        serverPeer = peer;
        State = StressBotState.Joining;
        var packet = RealtimeProtocol.EncodeJoinRequest(identity.Ticket);
        peer.Send(packet, DeliveryMethod.ReliableOrdered);
        RecordSent(packet.Length);
    }

    private void HandleDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (State is StressBotState.Completed or StressBotState.Failed)
        {
            return;
        }

        Fail(
            "connection_closed",
            $"SimulationWorker closed the stress bot connection with reason {disconnectInfo.Reason}.");
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
            packetsReceived++;
            bytesReceived += packet.Length;
            if (!RealtimeProtocol.TryReadMessageType(packet, out var messageType))
            {
                Fail("invalid_packet", "SimulationWorker sent a packet with an invalid realtime header.");
                return;
            }

            switch (messageType)
            {
                case RealtimeMessageType.JoinAccepted:
                    HandleJoinAccepted(packet, channel, deliveryMethod);
                    break;
                case RealtimeMessageType.JoinRejected:
                    HandleJoinRejected(packet);
                    break;
                case RealtimeMessageType.ServerDisconnect:
                    HandleServerDisconnect(packet);
                    break;
                case RealtimeMessageType.EntitySpawn:
                    HandleEntitySpawn(packet);
                    break;
                case RealtimeMessageType.EntityDespawn:
                    HandleEntityDespawn(packet);
                    break;
                case RealtimeMessageType.SimulationSnapshot:
                    HandleSnapshot(packet, channel, deliveryMethod);
                    break;
                case RealtimeMessageType.LeaveAccepted:
                    HandleLeaveAccepted(packet);
                    break;
                case RealtimeMessageType.LeaveRejected:
                    HandleLeaveRejected(packet);
                    break;
                default:
                    Fail("unexpected_message", $"SimulationWorker sent unexpected message type {messageType}.");
                    break;
            }
        }
        finally
        {
            reader.Recycle();
        }
    }

    private void HandleJoinAccepted(
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (State != StressBotState.Joining
            || channel != RealtimeProtocol.ControlChannel
            || deliveryMethod != DeliveryMethod.ReliableOrdered)
        {
            Fail("invalid_join_acceptance", "Join acceptance delivery is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeJoinAccepted(packet, out var session, out var error))
        {
            Fail("invalid_join_acceptance", error);
            return;
        }

        if (!Guid.TryParse(session.AccountId, out var accountId)
            || accountId != identity.AccountId
            || !Guid.TryParse(session.CharacterId, out var characterId)
            || characterId != identity.CharacterId
            || !string.Equals(session.CharacterName, identity.CharacterName, StringComparison.Ordinal)
            || !string.Equals(session.ShardId, options.ShardId, StringComparison.Ordinal)
            || !string.Equals(session.WorldId, options.WorldId, StringComparison.Ordinal))
        {
            Fail("join_identity_mismatch", "Join acceptance does not match the issued stress identity and assignment.");
            return;
        }

        joinedSession = session;
        State = StressBotState.Joined;
        var now = Stopwatch.GetTimestamp();
        joinLatencyMs = Stopwatch.GetElapsedTime(startedTimestamp, now).TotalMilliseconds;
        nextInputTimestamp = now;
    }

    private void HandleJoinRejected(byte[] packet)
    {
        if (RealtimeProtocol.TryDecodeJoinRejected(packet, out var rejection, out var error))
        {
            Fail(rejection.Code, rejection.Message);
            return;
        }

        Fail("invalid_join_rejection", error);
    }

    private void HandleServerDisconnect(byte[] packet)
    {
        if (RealtimeProtocol.TryDecodeServerDisconnect(packet, out var reason, out var error))
        {
            Fail(reason.Code, reason.Message);
            return;
        }

        Fail("invalid_server_disconnect", error);
    }

    private void HandleEntitySpawn(byte[] packet)
    {
        if (!RealtimeProtocol.TryDecodeEntitySpawn(packet, out _, out var error))
        {
            Fail("invalid_entity_spawn", error);
            return;
        }

        spawnPackets++;
    }

    private void HandleEntityDespawn(byte[] packet)
    {
        if (!RealtimeProtocol.TryDecodeEntityDespawn(packet, out _, out var error))
        {
            Fail("invalid_entity_despawn", error);
            return;
        }

        despawnPackets++;
    }

    private void HandleSnapshot(
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (State is not (StressBotState.Joined or StressBotState.Leaving))
        {
            return;
        }

        if (channel != RealtimeProtocol.UnreliableReceiveChannel
            || deliveryMethod != DeliveryMethod.Unreliable)
        {
            Fail("invalid_snapshot", "Snapshot delivery is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeSimulationSnapshot(packet, out var snapshot, out var error))
        {
            Fail("invalid_snapshot", error);
            return;
        }

        snapshotsReceived++;
        latestServerTick = snapshot.ServerTick;
        if (!hasSnapshotSequence)
        {
            hasSnapshotSequence = true;
            lastSnapshotSequence = snapshot.SnapshotSequence;
        }
        else if (snapshot.SnapshotSequence > lastSnapshotSequence)
        {
            if (snapshot.SnapshotSequence > lastSnapshotSequence + 1)
            {
                estimatedMissingSnapshots += snapshot.SnapshotSequence - lastSnapshotSequence - 1;
            }

            lastSnapshotSequence = snapshot.SnapshotSequence;
        }

        if (joinedSession is null)
        {
            return;
        }

        var localEntity = snapshot.Entities.FirstOrDefault(
            entity => entity.EntityId == joinedSession.ControlledEntityId);
        if (localEntity is not null)
        {
            AcknowledgeInputs(localEntity.LastProcessedInputSequence);
        }
    }

    private void HandleLeaveAccepted(byte[] packet)
    {
        if (State != StressBotState.Leaving)
        {
            Fail("invalid_leave_acceptance", "Leave acceptance is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeLeaveAccepted(packet, out var error))
        {
            Fail("invalid_leave_acceptance", error);
            return;
        }

        State = StressBotState.Completed;
    }

    private void HandleLeaveRejected(byte[] packet)
    {
        if (RealtimeProtocol.TryDecodeLeaveRejected(packet, out var rejection, out var error))
        {
            Fail(rejection.Code, rejection.Message);
            return;
        }

        Fail("invalid_leave_rejection", error);
    }

    private void SendDueInputs(long nowTimestamp)
    {
        if (joinedSession is null || serverPeer is null)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(1d / joinedSession.MovementSettings.TickRateHz);
        var intervalTicks = DurationToTimestampTicks(interval);
        var sentThisPoll = 0;
        while (nowTimestamp >= nextInputTimestamp && sentThisPoll < 3)
        {
            unchecked
            {
                nextInputSequence++;
                clientTick++;
            }

            var command = CreateMovementInput(nextInputSequence, clientTick);
            pendingInputs.Add(new PendingInput(command, nowTimestamp));
            if (pendingInputs.Count > MaximumTrackedUnacknowledgedInputs)
            {
                pendingInputs.RemoveAt(0);
                unacknowledgedInputsDropped++;
            }

            var batchCount = Math.Min(RealtimeProtocol.MaximumInputBatchSize, pendingInputs.Count);
            var batch = pendingInputs
                .Skip(pendingInputs.Count - batchCount)
                .Select(pending => pending.Command)
                .ToArray();
            var packet = RealtimeProtocol.EncodeMovementInputBatch(batch);
            serverPeer.Send(
                packet,
                RealtimeProtocol.MovementInputChannel,
                DeliveryMethod.Sequenced);
            RecordSent(packet.Length);
            nextInputTimestamp += intervalTicks;
            sentThisPoll++;
        }

        if (sentThisPoll == 3 && nowTimestamp >= nextInputTimestamp)
        {
            nextInputTimestamp = nowTimestamp + intervalTicks;
        }
    }

    private RealtimeMovementInput CreateMovementInput(uint sequence, uint tick)
    {
        var ticksPerPhase = Math.Max(1u, joinedSession!.MovementSettings.TickRateHz * 2u);
        var phase = (int)(((tick / ticksPerPhase) + (uint)(identity.BotIndex + options.Seed)) % 4u);
        var (moveX, moveY) = phase switch
        {
            0 => (0f, 1f),
            1 => (1f, 0f),
            2 => (0f, -1f),
            _ => (-1f, 0f)
        };
        var buttons = phase == 3
            ? RealtimeMovementButtons.Aim
            : RealtimeMovementButtons.Sprint;
        var jumpInterval = Math.Max(1u, joinedSession.MovementSettings.TickRateHz * 5u);
        if (phase != 3 && tick % jumpInterval == (uint)(identity.BotIndex % jumpInterval))
        {
            buttons |= RealtimeMovementButtons.Jump;
        }

        var yaw = (identity.BotIndex * 17f + tick * 1.5f) % 360f;
        return new RealtimeMovementInput(sequence, tick, moveX, moveY, yaw, buttons);
    }

    private void AcknowledgeInputs(uint acknowledgedSequence)
    {
        var now = Stopwatch.GetTimestamp();
        var removeCount = 0;
        foreach (var pending in pendingInputs)
        {
            if (pending.Command.InputSequence > acknowledgedSequence)
            {
                break;
            }

            var latencyMilliseconds = Stopwatch.GetElapsedTime(
                pending.SentTimestamp,
                now).TotalMilliseconds;
            inputAcknowledgementLatencies.Record(latencyMilliseconds);
            intervalInputAcknowledgementLatencies.Record(latencyMilliseconds);
            removeCount++;
        }

        if (removeCount > 0)
        {
            pendingInputs.RemoveRange(0, removeCount);
        }
    }

    private void RecordSent(int packetLength)
    {
        packetsSent++;
        bytesSent += packetLength;
    }

    private void Fail(string code, string message)
    {
        if (State is StressBotState.Completed or StressBotState.Failed)
        {
            return;
        }

        failureCode = string.IsNullOrWhiteSpace(code) ? "stress_bot_failed" : code;
        failureMessage = string.IsNullOrWhiteSpace(message)
            ? "The stress bot failed without a detailed message."
            : message;
        State = StressBotState.Failed;
    }

    private static long AddDuration(long timestamp, TimeSpan duration)
    {
        return timestamp + DurationToTimestampTicks(duration);
    }

    private static long DurationToTimestampTicks(TimeSpan duration)
    {
        return (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed record PendingInput(
        RealtimeMovementInput Command,
        long SentTimestamp);
}

public sealed record StressBotSnapshot(
    int BotIndex,
    string CharacterName,
    StressBotState State,
    double? JoinLatencyMs,
    long PacketsSent,
    long BytesSent,
    long PacketsReceived,
    long BytesReceived,
    long SnapshotsReceived,
    long EstimatedMissingSnapshots,
    long SpawnPackets,
    long DespawnPackets,
    long UnacknowledgedInputsDropped,
    uint LatestServerTick,
    string? FailureCode,
    string? FailureMessage);
