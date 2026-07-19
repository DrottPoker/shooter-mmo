using System.Diagnostics;
using System.Net;
using LiteNetLib;
using ShooterMmo.GameProtocol;

namespace ShooterMmo.Tools.SimulationBotClient;

public sealed class SimulationBotClient : IDisposable
{
    private const int MaximumTrackedUnacknowledgedInputs = 256;

    private readonly SimulationBotConnectionOptions connectionOptions;
    private readonly SimulationBotIdentity identity;
    private readonly ISimulationBotInputSource inputSource;
    private readonly Action<double>? inputAcknowledged;
    private readonly ISimulationBotRealtimeObserver? realtimeObserver;
    private readonly EventBasedNetListener listener = new();
    private readonly NetManager client;
    private readonly List<PendingInput> pendingInputs = [];
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

    public SimulationBotClient(
        SimulationBotConnectionOptions connectionOptions,
        SimulationBotIdentity identity,
        ISimulationBotInputSource inputSource,
        Action<double>? inputAcknowledged = null,
        ISimulationBotRealtimeObserver? realtimeObserver = null)
    {
        this.connectionOptions = connectionOptions
            ?? throw new ArgumentNullException(nameof(connectionOptions));
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.inputSource = inputSource ?? throw new ArgumentNullException(nameof(inputSource));
        this.inputAcknowledged = inputAcknowledged;
        this.realtimeObserver = realtimeObserver;
        ValidateOptions(connectionOptions, identity);

        client = new NetManager(listener)
        {
            ChannelsCount = RealtimeProtocol.ChannelCount,
            DisconnectTimeout = (int)connectionOptions.JoinAndLeaveTimeout.TotalMilliseconds,
            MaxPacketPerManualReceive = 256
        };

        listener.PeerConnectedEvent += HandleConnected;
        listener.PeerDisconnectedEvent += HandleDisconnected;
        listener.NetworkReceiveEvent += HandleNetworkReceive;
    }

    public SimulationBotClientState State { get; private set; } =
        SimulationBotClientState.Created;

    public void Start()
    {
        ThrowIfDisposed();
        if (State != SimulationBotClientState.Created)
        {
            throw new InvalidOperationException("Simulation bot client can only be started once.");
        }

        startedTimestamp = Stopwatch.GetTimestamp();
        lastPollTimestamp = startedTimestamp;
        joinDeadlineTimestamp = AddDuration(
            startedTimestamp,
            connectionOptions.JoinAndLeaveTimeout);
        if (!client.Start(IPAddress.Any, IPAddress.IPv6Any, 0, manualMode: true))
        {
            Fail("client_start_failed", "LiteNetLib could not start the simulation bot transport.");
            return;
        }

        State = SimulationBotClientState.Connecting;
        serverPeer = client.Connect(
            connectionOptions.Host,
            connectionOptions.UdpPort,
            RealtimeProtocol.ConnectionKey);
        if (serverPeer is null)
        {
            Fail(
                "connection_start_failed",
                "LiteNetLib could not create the simulation bot connection.");
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

        if (State is SimulationBotClientState.Connecting or SimulationBotClientState.Joining
            && nowTimestamp >= joinDeadlineTimestamp)
        {
            Fail(
                "join_timeout",
                "SimulationWorker did not accept the simulation bot before the join timeout.");
            return;
        }

        if (State == SimulationBotClientState.Joined)
        {
            SendDueInputs(nowTimestamp);
        }
        else if (State == SimulationBotClientState.Leaving
            && nowTimestamp >= leaveDeadlineTimestamp)
        {
            Fail(
                "leave_timeout",
                "SimulationWorker did not acknowledge the simulation bot leave request.");
        }
    }

    public void BeginLeave(long nowTimestamp)
    {
        ThrowIfDisposed();
        if (State != SimulationBotClientState.Joined
            || serverPeer is null
            || joinedSession is null)
        {
            return;
        }

        var packet = RealtimeProtocol.EncodeLeaveRequest(joinedSession.SimulationSessionId);
        serverPeer.Send(packet, DeliveryMethod.ReliableOrdered);
        RecordSent(packet.Length);
        State = SimulationBotClientState.Leaving;
        leaveDeadlineTimestamp = AddDuration(
            nowTimestamp,
            connectionOptions.JoinAndLeaveTimeout);
    }

    public bool SendItemOperation(RealtimeItemOperationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return SendJoinedControl(RealtimeProtocol.EncodeItemOperationIntent(intent));
    }

    public bool SendCorpseInteraction(RealtimeCorpseInteractionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return SendJoinedControl(RealtimeProtocol.EncodeCorpseInteractionIntent(intent));
    }

    public SimulationBotClientSnapshot Capture()
    {
        return new SimulationBotClientSnapshot(
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
        State = SimulationBotClientState.Joining;
        var packet = RealtimeProtocol.EncodeJoinRequest(identity.Ticket);
        peer.Send(packet, DeliveryMethod.ReliableOrdered);
        RecordSent(packet.Length);
    }

    private void HandleDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (State is SimulationBotClientState.Completed or SimulationBotClientState.Failed)
        {
            return;
        }

        Fail(
            "connection_closed",
            $"SimulationWorker closed the simulation bot connection with reason {disconnectInfo.Reason}.");
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
                Fail(
                    "invalid_packet",
                    "SimulationWorker sent a packet with an invalid realtime header.");
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
                case RealtimeMessageType.CarryStateChanged:
                    HandleCarryStateChanged(packet, channel, deliveryMethod);
                    break;
                case RealtimeMessageType.ItemOperationResult:
                    HandleItemOperationResult(packet, channel, deliveryMethod);
                    break;
                case RealtimeMessageType.CorpsePresenceSnapshotChunk:
                    HandleCorpsePresence(packet, channel, deliveryMethod);
                    break;
                case RealtimeMessageType.CorpseInteractionResult:
                    HandleCorpseInteractionResult(packet, channel, deliveryMethod);
                    break;
                case RealtimeMessageType.CorpseViewStateChunk:
                    HandleCorpseViewState(packet, channel, deliveryMethod);
                    break;
                case RealtimeMessageType.CorpseViewClosed:
                    HandleCorpseViewClosed(packet, channel, deliveryMethod);
                    break;
                case RealtimeMessageType.WorldActorSpawn:
                    HandleWorldActorSpawn(packet, channel, deliveryMethod);
                    break;
                case RealtimeMessageType.WorldActorState:
                    HandleWorldActorState(packet, channel, deliveryMethod);
                    break;
                case RealtimeMessageType.WorldActorDespawn:
                    HandleWorldActorDespawn(packet, channel, deliveryMethod);
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
                    Fail(
                        "unexpected_message",
                        $"SimulationWorker sent unexpected message type {messageType}.");
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
        if (State != SimulationBotClientState.Joining
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
            || !string.Equals(session.ShardId, identity.ShardId, StringComparison.Ordinal)
            || !string.Equals(session.WorldId, identity.WorldId, StringComparison.Ordinal))
        {
            Fail(
                "join_identity_mismatch",
                "Join acceptance does not match the issued simulation bot identity and assignment.");
            return;
        }

        joinedSession = session;
        State = SimulationBotClientState.Joined;
        var now = Stopwatch.GetTimestamp();
        joinLatencyMs = Stopwatch.GetElapsedTime(startedTimestamp, now).TotalMilliseconds;
        nextInputTimestamp = now;
        realtimeObserver?.OnJoined(session);
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

    private void HandleCarryStateChanged(
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (State != SimulationBotClientState.Joined
            || channel != RealtimeProtocol.ControlChannel
            || deliveryMethod != DeliveryMethod.ReliableOrdered)
        {
            Fail("invalid_carry_state", "Carry-state delivery is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeCarryStateChanged(packet, out _, out var error))
        {
            Fail("invalid_carry_state", error);
        }
    }

    private void HandleItemOperationResult(
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (!IsValidJoinedControlDelivery(channel, deliveryMethod))
        {
            Fail("invalid_item_operation_result", "Item operation result delivery is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeItemOperationResult(packet, out var result, out var error))
        {
            Fail("invalid_item_operation_result", error);
            return;
        }

        realtimeObserver?.OnItemOperationResult(result);
    }

    private void HandleCorpsePresence(
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (!IsValidJoinedControlDelivery(channel, deliveryMethod))
        {
            Fail(
                "invalid_corpse_presence",
                "Corpse presence delivery is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeCorpsePresenceSnapshotChunk(
                packet,
                out var chunk,
                out var error))
        {
            Fail("invalid_corpse_presence", error);
            return;
        }

        realtimeObserver?.OnCorpsePresence(chunk);
    }

    private void HandleCorpseInteractionResult(
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (!IsValidJoinedControlDelivery(channel, deliveryMethod))
        {
            Fail(
                "invalid_corpse_interaction_result",
                "Corpse interaction result delivery is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeCorpseInteractionResult(
                packet,
                out var result,
                out var error))
        {
            Fail("invalid_corpse_interaction_result", error);
            return;
        }

        realtimeObserver?.OnCorpseInteractionResult(result);
    }

    private void HandleCorpseViewState(
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (!IsValidJoinedControlDelivery(channel, deliveryMethod))
        {
            Fail("invalid_corpse_view_state", "Corpse view-state delivery is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeCorpseViewStateChunk(packet, out var chunk, out var error))
        {
            Fail("invalid_corpse_view_state", error);
            return;
        }

        realtimeObserver?.OnCorpseViewState(chunk);
    }

    private void HandleCorpseViewClosed(
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (!IsValidJoinedControlDelivery(channel, deliveryMethod))
        {
            Fail("invalid_corpse_view_closed", "Corpse view closure delivery is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeCorpseViewClosed(packet, out var closed, out var error))
        {
            Fail("invalid_corpse_view_closed", error);
            return;
        }

        realtimeObserver?.OnCorpseViewClosed(closed);
    }

    private void HandleWorldActorSpawn(
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (!IsValidJoinedControlDelivery(channel, deliveryMethod))
        {
            Fail(
                "invalid_world_actor_spawn",
                "World actor spawn delivery is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeWorldActorSpawn(packet, out _, out var error))
        {
            Fail("invalid_world_actor_spawn", error);
            return;
        }

        spawnPackets++;
    }

    private void HandleWorldActorState(
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (!IsValidJoinedControlDelivery(channel, deliveryMethod))
        {
            Fail(
                "invalid_world_actor_state",
                "World actor state delivery is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeWorldActorState(packet, out _, out var error))
        {
            Fail("invalid_world_actor_state", error);
        }
    }

    private void HandleWorldActorDespawn(
        byte[] packet,
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        if (!IsValidJoinedControlDelivery(channel, deliveryMethod))
        {
            Fail(
                "invalid_world_actor_despawn",
                "World actor despawn delivery is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeWorldActorDespawn(packet, out _, out var error))
        {
            Fail("invalid_world_actor_despawn", error);
            return;
        }

        despawnPackets++;
    }

    private bool IsValidJoinedControlDelivery(
        byte channel,
        DeliveryMethod deliveryMethod)
    {
        return State == SimulationBotClientState.Joined
            && channel == RealtimeProtocol.ControlChannel
            && deliveryMethod == DeliveryMethod.ReliableOrdered;
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
        if (State is not (SimulationBotClientState.Joined or SimulationBotClientState.Leaving))
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
        if (State != SimulationBotClientState.Leaving)
        {
            Fail("invalid_leave_acceptance", "Leave acceptance is invalid.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeLeaveAccepted(packet, out var error))
        {
            Fail("invalid_leave_acceptance", error);
            return;
        }

        State = SimulationBotClientState.Completed;
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

            var command = inputSource.CreateInput(
                nextInputSequence,
                clientTick,
                joinedSession);
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

            inputAcknowledged?.Invoke(
                Stopwatch.GetElapsedTime(pending.SentTimestamp, now).TotalMilliseconds);
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

    private bool SendJoinedControl(byte[] packet)
    {
        ThrowIfDisposed();
        if (State != SimulationBotClientState.Joined
            || serverPeer is null
            || joinedSession is null)
        {
            return false;
        }

        serverPeer.Send(packet, RealtimeProtocol.ControlChannel, DeliveryMethod.ReliableOrdered);
        RecordSent(packet.Length);
        return true;
    }

    private void Fail(string code, string message)
    {
        if (State is SimulationBotClientState.Completed or SimulationBotClientState.Failed)
        {
            return;
        }

        failureCode = string.IsNullOrWhiteSpace(code) ? "simulation_bot_failed" : code;
        failureMessage = string.IsNullOrWhiteSpace(message)
            ? "The simulation bot failed without a detailed message."
            : message;
        State = SimulationBotClientState.Failed;
    }

    private static void ValidateOptions(
        SimulationBotConnectionOptions options,
        SimulationBotIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(options.Host)
            || options.UdpPort is < 1 or > ushort.MaxValue
            || options.JoinAndLeaveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Simulation bot connection options are invalid.", nameof(options));
        }

        if (identity.BotIndex < 1
            || identity.AccountId == Guid.Empty
            || identity.CharacterId == Guid.Empty
            || string.IsNullOrWhiteSpace(identity.CharacterName)
            || string.IsNullOrWhiteSpace(identity.Ticket)
            || string.IsNullOrWhiteSpace(identity.ShardId)
            || string.IsNullOrWhiteSpace(identity.WorldId))
        {
            throw new ArgumentException("Simulation bot identity is invalid.", nameof(identity));
        }
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
