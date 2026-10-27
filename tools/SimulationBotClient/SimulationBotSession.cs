using System.Diagnostics;
using Mmorpg.GameProtocol;

namespace Mmorpg.Tools.SimulationBotClient;

// The protocol side of one headless bot connection, apart from its transport: the join and
// leave flow, the movement inputs and their acknowledgement, and the checks on every message
// the worker sends. A message must travel on the channel and with the delivery that its
// protocol descriptor names and must decode, as the Unity client requires. Timestamps are
// Stopwatch timestamps that the caller passes in, so the session runs the same in tests.
public sealed class SimulationBotSession
{
    private const int MaximumTrackedUnacknowledgedInputs = 256;
    private const int MaximumInputsPerUpdate = 3;

    private readonly SimulationBotIdentity identity;
    private readonly TimeSpan joinAndLeaveTimeout;
    private readonly ISimulationBotInputSource inputSource;
    private readonly Action<double>? inputAcknowledged;
    private readonly ISimulationBotRealtimeObserver? realtimeObserver;
    private readonly List<PendingInput> pendingInputs = [];
    private ISimulationBotTransport? transport;
    private RealtimeJoinAccepted? joinedSession;
    private long startedTimestamp;
    private long joinDeadlineTimestamp;
    private long nextInputTimestamp;
    private long leaveDeadlineTimestamp;
    private uint nextInputSequence;
    private uint clientTick;
    private uint lastSnapshotSequence;
    private bool hasSnapshotSequence;
    private SimulationBotPosition latestPosition;
    private uint confirmedPathId;
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

    public SimulationBotSession(
        SimulationBotIdentity identity,
        TimeSpan joinAndLeaveTimeout,
        ISimulationBotInputSource inputSource,
        Action<double>? inputAcknowledged = null,
        ISimulationBotRealtimeObserver? realtimeObserver = null)
    {
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.inputSource = inputSource ?? throw new ArgumentNullException(nameof(inputSource));
        if (joinAndLeaveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(joinAndLeaveTimeout),
                "The join and leave timeout must be positive.");
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

        this.joinAndLeaveTimeout = joinAndLeaveTimeout;
        this.inputAcknowledged = inputAcknowledged;
        this.realtimeObserver = realtimeObserver;
    }

    public SimulationBotClientState State { get; private set; } =
        SimulationBotClientState.Created;

    // The transport is connecting; the join must complete before the join timeout.
    public void Start(long nowTimestamp)
    {
        if (State != SimulationBotClientState.Created)
        {
            throw new InvalidOperationException("Simulation bot session can only be started once.");
        }

        startedTimestamp = nowTimestamp;
        joinDeadlineTimestamp = AddDuration(nowTimestamp, joinAndLeaveTimeout);
        State = SimulationBotClientState.Connecting;
    }

    // The transport connected; the session asks to join with its one-time ticket.
    public void OnConnected(ISimulationBotTransport connectedTransport)
    {
        ArgumentNullException.ThrowIfNull(connectedTransport);
        if (State != SimulationBotClientState.Connecting)
        {
            return;
        }

        transport = connectedTransport;
        State = SimulationBotClientState.Joining;
        Send(RealtimeMessageType.JoinRequest, RealtimeProtocol.EncodeJoinRequest(identity.Ticket));
    }

    public void OnDisconnected(string reason)
    {
        Fail(
            "connection_closed",
            $"SimulationWorker closed the simulation bot connection with reason {reason}.");
    }

    // Ends the join or leave that ran out of time, and sends the movement inputs that are due.
    public void Update(long nowTimestamp)
    {
        if (State is SimulationBotClientState.Connecting or SimulationBotClientState.Joining
            && nowTimestamp >= joinDeadlineTimestamp)
        {
            Fail(
                "join_timeout",
                "SimulationWorker did not accept the simulation bot before the join timeout.");
        }
        else if (State == SimulationBotClientState.Joined)
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
        if (State != SimulationBotClientState.Joined || joinedSession is null)
        {
            return;
        }

        Send(
            RealtimeMessageType.LeaveRequest,
            RealtimeProtocol.EncodeLeaveRequest(joinedSession.SimulationSessionId));
        State = SimulationBotClientState.Leaving;
        leaveDeadlineTimestamp = AddDuration(nowTimestamp, joinAndLeaveTimeout);
    }

    public bool SendItemOperation(RealtimeItemOperationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return SendJoined(
            RealtimeMessageType.ItemOperationIntent,
            RealtimeProtocol.EncodeItemOperationIntent(intent));
    }

    public bool SendCorpseInteraction(RealtimeCorpseInteractionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return SendJoined(
            RealtimeMessageType.CorpseInteractionIntent,
            RealtimeProtocol.EncodeCorpseInteractionIntent(intent));
    }

    // Handles one received packet. After the session completed or failed, packets only count
    // as traffic, so the first failure reason stays.
    public void Receive(
        byte[] packet,
        byte channel,
        RealtimeDelivery delivery,
        long nowTimestamp)
    {
        ArgumentNullException.ThrowIfNull(packet);
        packetsReceived++;
        bytesReceived += packet.Length;
        if (State is SimulationBotClientState.Completed or SimulationBotClientState.Failed)
        {
            return;
        }

        if (!RealtimeProtocol.TryReadMessageType(packet, out var messageType)
            || !RealtimeProtocol.TryGetDescriptor(messageType, out var descriptor))
        {
            Fail(
                "invalid_packet",
                "SimulationWorker sent a packet with an invalid realtime header.");
            return;
        }

        if (descriptor.Direction != RealtimeMessageDirection.ServerToClient)
        {
            Fail(
                "unexpected_message",
                $"SimulationWorker sent {messageType}, which only clients send.");
            return;
        }

        if (channel != descriptor.Channel || delivery != descriptor.Delivery)
        {
            Fail(
                "invalid_delivery",
                $"{messageType} arrived on channel {channel} with {delivery} delivery; the protocol requires channel {descriptor.Channel} with {descriptor.Delivery}.");
            return;
        }

        switch (messageType)
        {
            case RealtimeMessageType.JoinAccepted:
                HandleJoinAccepted(packet, nowTimestamp);
                return;
            case RealtimeMessageType.JoinRejected:
                HandleJoinRejected(packet);
                return;
            case RealtimeMessageType.LeaveAccepted:
                HandleLeaveAccepted(packet);
                return;
            case RealtimeMessageType.LeaveRejected:
                HandleLeaveRejected(packet);
                return;
            case RealtimeMessageType.ServerDisconnect:
                HandleServerDisconnect(packet);
                return;
        }

        if (State is not (SimulationBotClientState.Joined or SimulationBotClientState.Leaving))
        {
            // A snapshot can overtake the reliable join acceptance, so one that arrives
            // early is dropped. Every other message must follow the acceptance.
            if (descriptor.Delivery != RealtimeDelivery.Unreliable)
            {
                Fail(
                    "unexpected_message",
                    $"SimulationWorker sent {messageType} before it accepted the join.");
            }

            return;
        }

        // The worker keeps sending until it has released the session, so every message
        // of a joined session is also valid while a leave is pending.
        HandleSessionMessage(messageType, packet, nowTimestamp);
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

    public void Fail(string code, string message)
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

    // Bots act only on snapshots and the results of their own operations, but every other
    // message must still decode. A server-to-client message type that is missing here fails
    // the bot, and a test feeds every protocol vector through this switch.
    private void HandleSessionMessage(
        RealtimeMessageType messageType,
        byte[] packet,
        long nowTimestamp)
    {
        switch (messageType)
        {
            case RealtimeMessageType.SimulationSnapshot:
                if (Decode<RealtimeSimulationSnapshot>(packet, "invalid_snapshot", RealtimeProtocol.TryDecodeSimulationSnapshot, out _))
                {
                    snapshotsReceived++;
                }

                break;
            case RealtimeMessageType.OwnerSimulationSnapshot:
                if (Decode<RealtimeOwnerSimulationSnapshot>(packet, "invalid_owner_snapshot", RealtimeProtocol.TryDecodeOwnerSimulationSnapshot, out var ownerSnapshot))
                {
                    HandleOwnerSnapshot(ownerSnapshot, nowTimestamp);
                }

                break;
            case RealtimeMessageType.EntitySpawn:
                if (Decode<RealtimeEntitySpawn>(packet, "invalid_entity_spawn", RealtimeProtocol.TryDecodeEntitySpawn, out _))
                {
                    spawnPackets++;
                }

                break;
            case RealtimeMessageType.EntityDespawn:
                if (Decode<RealtimeEntityDespawn>(packet, "invalid_entity_despawn", RealtimeProtocol.TryDecodeEntityDespawn, out _))
                {
                    despawnPackets++;
                }

                break;
            case RealtimeMessageType.WorldActorSpawn:
                if (Decode<RealtimeWorldActorSpawn>(packet, "invalid_world_actor_spawn", RealtimeProtocol.TryDecodeWorldActorSpawn, out _))
                {
                    spawnPackets++;
                }

                break;
            case RealtimeMessageType.WorldActorDespawn:
                if (Decode<RealtimeWorldActorDespawn>(packet, "invalid_world_actor_despawn", RealtimeProtocol.TryDecodeWorldActorDespawn, out _))
                {
                    despawnPackets++;
                }

                break;
            case RealtimeMessageType.WorldActorState:
                Decode<RealtimeWorldActorState>(packet, "invalid_world_actor_state", RealtimeProtocol.TryDecodeWorldActorState, out _);
                break;
            case RealtimeMessageType.ItemOperationResult:
                if (Decode<RealtimeItemOperationResult>(packet, "invalid_item_operation_result", RealtimeProtocol.TryDecodeItemOperationResult, out var itemResult))
                {
                    realtimeObserver?.OnItemOperationResult(itemResult);
                }

                break;
            case RealtimeMessageType.CorpsePresenceSnapshotChunk:
                if (Decode<RealtimeCorpsePresenceSnapshotChunk>(packet, "invalid_corpse_presence", RealtimeProtocol.TryDecodeCorpsePresenceSnapshotChunk, out var presence))
                {
                    realtimeObserver?.OnCorpsePresence(presence);
                }

                break;
            case RealtimeMessageType.CorpseInteractionResult:
                if (Decode<RealtimeCorpseInteractionResult>(packet, "invalid_corpse_interaction_result", RealtimeProtocol.TryDecodeCorpseInteractionResult, out var corpseResult))
                {
                    realtimeObserver?.OnCorpseInteractionResult(corpseResult);
                }

                break;
            case RealtimeMessageType.CorpseViewStateChunk:
                if (Decode<RealtimeCorpseViewStateChunk>(packet, "invalid_corpse_view_state", RealtimeProtocol.TryDecodeCorpseViewStateChunk, out var viewState))
                {
                    realtimeObserver?.OnCorpseViewState(viewState);
                }

                break;
            case RealtimeMessageType.CorpseViewClosed:
                if (Decode<RealtimeCorpseViewClosed>(packet, "invalid_corpse_view_closed", RealtimeProtocol.TryDecodeCorpseViewClosed, out var viewClosed))
                {
                    realtimeObserver?.OnCorpseViewClosed(viewClosed);
                }

                break;
            case RealtimeMessageType.CarryStateChanged:
                Decode<RealtimeCarryState>(packet, "invalid_carry_state", RealtimeProtocol.TryDecodeCarryStateChanged, out _);
                break;
            case RealtimeMessageType.OwnerItemQuantityChanged:
                Decode<RealtimeOwnerItemQuantityChanged>(packet, "invalid_owner_item_quantity", RealtimeProtocol.TryDecodeOwnerItemQuantityChanged, out _);
                break;
            case RealtimeMessageType.OwnerItemStateChanged:
                Decode<RealtimeOwnerItemStateChanged>(packet, "invalid_owner_item_state", RealtimeProtocol.TryDecodeOwnerItemStateChanged, out _);
                break;
            case RealtimeMessageType.EntityAppearanceChanged:
                Decode<RealtimeEntityAppearanceChanged>(packet, "invalid_entity_appearance", RealtimeProtocol.TryDecodeEntityAppearanceChanged, out _);
                break;
            case RealtimeMessageType.AppearanceIntentResult:
                Decode<RealtimeAppearanceIntentResult>(packet, "invalid_appearance_intent_result", RealtimeProtocol.TryDecodeAppearanceIntentResult, out _);
                break;
            case RealtimeMessageType.AdminIntentResult:
                Decode<RealtimeAdminIntentResult>(packet, "invalid_admin_intent_result", RealtimeProtocol.TryDecodeAdminIntentResult, out _);
                break;
            case RealtimeMessageType.AdminStateChanged:
                Decode<RealtimeAdminStateChanged>(packet, "invalid_admin_state", RealtimeProtocol.TryDecodeAdminStateChanged, out _);
                break;
            case RealtimeMessageType.WorldInteractionOpened:
                Decode<RealtimeWorldInteractionOpened>(packet, "invalid_world_interaction_opened", RealtimeProtocol.TryDecodeWorldInteractionOpened, out _);
                break;
            case RealtimeMessageType.WorldInteractionResult:
                Decode<RealtimeWorldInteractionResult>(packet, "invalid_world_interaction_result", RealtimeProtocol.TryDecodeWorldInteractionResult, out _);
                break;
            case RealtimeMessageType.WorldInteractionClosed:
                Decode<RealtimeWorldInteractionClosed>(packet, "invalid_world_interaction_closed", RealtimeProtocol.TryDecodeWorldInteractionClosed, out _);
                break;
            case RealtimeMessageType.GatheringState:
                Decode<RealtimeGatheringState>(packet, "invalid_gathering_state", RealtimeProtocol.TryDecodeGatheringState, out _);
                break;
            case RealtimeMessageType.CombatIntentResult:
                Decode<RealtimeCombatIntentResult>(packet, "invalid_combat_intent_result", RealtimeProtocol.TryDecodeCombatIntentResult, out _);
                break;
            case RealtimeMessageType.CombatantState:
                Decode<RealtimeCombatantState>(packet, "invalid_combatant_state", RealtimeProtocol.TryDecodeCombatantState, out _);
                break;
            case RealtimeMessageType.CombatEventBatch:
                Decode<RealtimeCombatEventBatch>(packet, "invalid_combat_event_batch", RealtimeProtocol.TryDecodeCombatEventBatch, out _);
                break;
            case RealtimeMessageType.OwnerCombatState:
                Decode<RealtimeOwnerCombatState>(packet, "invalid_owner_combat_state", RealtimeProtocol.TryDecodeOwnerCombatState, out _);
                break;
            default:
                Fail(
                    "unexpected_message",
                    $"SimulationWorker sent message type {messageType}, which the simulation bot does not handle.");
                break;
        }
    }

    private delegate bool Decoder<T>(byte[] packet, out T value, out string error);

    private bool Decode<T>(byte[] packet, string code, Decoder<T> decode, out T value)
    {
        if (decode(packet, out value, out var error))
        {
            return true;
        }

        Fail(code, error);
        return false;
    }

    private void HandleJoinAccepted(byte[] packet, long nowTimestamp)
    {
        if (State != SimulationBotClientState.Joining)
        {
            Fail("invalid_join_acceptance", "Join acceptance arrived outside a pending join.");
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
        latestPosition = new SimulationBotPosition(
            session.InitialPlayerState.PositionX,
            session.InitialPlayerState.PositionY,
            session.InitialPlayerState.PositionZ);
        confirmedPathId = 0;
        State = SimulationBotClientState.Joined;
        joinLatencyMs = ElapsedMilliseconds(startedTimestamp, nowTimestamp);
        nextInputTimestamp = nowTimestamp;
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

    private void HandleLeaveAccepted(byte[] packet)
    {
        if (State != SimulationBotClientState.Leaving)
        {
            Fail("invalid_leave_acceptance", "Leave acceptance arrived without a pending leave.");
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

    private void HandleServerDisconnect(byte[] packet)
    {
        if (RealtimeProtocol.TryDecodeServerDisconnect(packet, out var reason, out var error))
        {
            Fail(reason.Code, reason.Message);
            return;
        }

        Fail("invalid_server_disconnect", error);
    }

    private void HandleOwnerSnapshot(RealtimeOwnerSimulationSnapshot snapshot, long nowTimestamp)
    {
        snapshotsReceived++;
        var isNewest = true;
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
        else
        {
            isNewest = false;
        }

        // A reordered older snapshot must not move the bot back or unconfirm its path.
        if (isNewest)
        {
            latestServerTick = snapshot.ServerTick;
            latestPosition = new SimulationBotPosition(
                snapshot.State.PositionX,
                snapshot.State.PositionY,
                snapshot.State.PositionZ);
            confirmedPathId = snapshot.PathProgress.PathId;
        }

        AcknowledgeInputs(snapshot.LastProcessedInputSequence, nowTimestamp);
    }

    private void SendDueInputs(long nowTimestamp)
    {
        if (joinedSession is null)
        {
            return;
        }

        var intervalTicks = DurationToTimestampTicks(
            TimeSpan.FromSeconds(1d / joinedSession.MovementSettings.TickRateHz));
        var sentThisUpdate = 0;
        while (nowTimestamp >= nextInputTimestamp && sentThisUpdate < MaximumInputsPerUpdate)
        {
            unchecked
            {
                nextInputSequence++;
                clientTick++;
            }

            var path = inputSource.SelectPath(clientTick, joinedSession, latestPosition);
            var command = new RealtimeMovementInput(
                nextInputSequence,
                clientTick,
                path?.PathId ?? 0);
            pendingInputs.Add(new PendingInput(command, nowTimestamp));
            if (pendingInputs.Count > MaximumTrackedUnacknowledgedInputs)
            {
                pendingInputs.RemoveAt(0);
                unacknowledgedInputsDropped++;
            }

            var batchCount = Math.Min(RealtimeProtocol.MaximumInputBatchSize, pendingInputs.Count);
            var batch = new RealtimeMovementInput[batchCount];
            for (var index = 0; index < batchCount; index++)
            {
                batch[index] = pendingInputs[pendingInputs.Count - batchCount + index].Command;
            }

            // The path travels with every batch until an owner snapshot confirms it.
            Send(
                RealtimeMessageType.MovementInputBatch,
                RealtimeProtocol.EncodeMovementInputBatch(new RealtimeMovementInputBatch(
                    batch,
                    path is not null && path.PathId != confirmedPathId ? path : null)));
            nextInputTimestamp += intervalTicks;
            sentThisUpdate++;
        }

        // A bot that fell far behind skips the missed inputs instead of bursting them.
        if (sentThisUpdate == MaximumInputsPerUpdate && nowTimestamp >= nextInputTimestamp)
        {
            nextInputTimestamp = nowTimestamp + intervalTicks;
        }
    }

    private void AcknowledgeInputs(uint acknowledgedSequence, long nowTimestamp)
    {
        var removeCount = 0;
        foreach (var pending in pendingInputs)
        {
            if (pending.Command.InputSequence > acknowledgedSequence)
            {
                break;
            }

            inputAcknowledged?.Invoke(ElapsedMilliseconds(pending.SentTimestamp, nowTimestamp));
            removeCount++;
        }

        if (removeCount > 0)
        {
            pendingInputs.RemoveRange(0, removeCount);
        }
    }

    private bool SendJoined(RealtimeMessageType messageType, byte[] packet)
    {
        if (State != SimulationBotClientState.Joined || joinedSession is null)
        {
            return false;
        }

        Send(messageType, packet);
        return true;
    }

    // Every message leaves on the channel and with the delivery that the protocol names.
    private void Send(RealtimeMessageType messageType, byte[] packet)
    {
        if (transport is null || !RealtimeProtocol.TryGetDescriptor(messageType, out var descriptor))
        {
            throw new InvalidOperationException($"The simulation bot cannot send {messageType} now.");
        }

        transport.Send(packet, descriptor.Channel, descriptor.Delivery);
        packetsSent++;
        bytesSent += packet.Length;
    }

    private static double ElapsedMilliseconds(long startTimestamp, long endTimestamp)
    {
        return Math.Max(0, endTimestamp - startTimestamp) * 1000d / Stopwatch.Frequency;
    }

    private static long AddDuration(long timestamp, TimeSpan duration)
    {
        return timestamp + DurationToTimestampTicks(duration);
    }

    private static long DurationToTimestampTicks(TimeSpan duration)
    {
        return (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
    }

    private sealed record PendingInput(
        RealtimeMovementInput Command,
        long SentTimestamp);
}
