using System;
using System.Collections;
using System.Collections.Generic;
using LiteNetLib;
using ShooterMmo.Api;
using ShooterMmo.Diagnostics;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using UnityEngine;

namespace ShooterMmo.Networking
{
    public enum RealtimeConnectionState
    {
        Disconnected,
        Connecting,
        Joining,
        Joined,
        Leaving
    }

    public sealed class RealtimeSimulationClient : MonoBehaviour
    {
        private EventBasedNetListener listener;
        private NetManager client;
        private NetPeer serverPeer;
        private RealtimeClientError operationError;
        private ActiveSimulationSessionResponse joinedSession;
        private RealtimeClientError pendingUnexpectedDisconnect;
        private string pendingJoinTicket;
        private JoinShardResponse pendingPlacement;
        private float operationDeadline;
        private bool transportStarted;
        private bool stopTransportAfterPoll;
        private bool leaveAccepted;
        private readonly Dictionary<ulong, RealtimeEntitySpawn> spawnedEntities =
            new Dictionary<ulong, RealtimeEntitySpawn>();

        public event Action<RealtimeClientError> UnexpectedlyDisconnected;

        public event Action<RealtimeSimulationSnapshot> SimulationSnapshotReceived;

        public event Action<RealtimeEntitySpawn> EntitySpawned;

        public event Action<RealtimeEntityDespawn> EntityDespawned;

        public RealtimeConnectionState State { get; private set; }

        public string ConnectedHost { get; private set; } = string.Empty;

        public int ConnectedPort { get; private set; }

        public NetworkMovementSession MovementSession { get; private set; }

        public uint LatestServerTick { get; private set; }

        public IReadOnlyCollection<RealtimeEntitySpawn> SpawnedEntities
        {
            get { return new List<RealtimeEntitySpawn>(spawnedEntities.Values); }
        }

        public bool IsJoined
        {
            get { return State == RealtimeConnectionState.Joined; }
        }

        public void DisconnectForClientFailure(string code, string message)
        {
            if (State == RealtimeConnectionState.Disconnected)
            {
                return;
            }

            FailProtocol(code, message);
        }

        private void Awake()
        {
            listener = new EventBasedNetListener();
            client = new NetManager(listener);
            listener.PeerConnectedEvent += OnPeerConnected;
            listener.PeerDisconnectedEvent += OnPeerDisconnected;
            listener.NetworkReceiveEvent += OnNetworkReceive;
            listener.NetworkErrorEvent += OnNetworkError;
            client.ChannelsCount = RealtimeProtocol.ChannelCount;
            State = RealtimeConnectionState.Disconnected;
        }

        private void Update()
        {
            if (transportStarted)
            {
                client.PollEvents();
            }

            if (State != RealtimeConnectionState.Disconnected
                && State != RealtimeConnectionState.Joined
                && Time.realtimeSinceStartup >= operationDeadline)
            {
                FailCurrentOperation(new RealtimeClientError(
                    RealtimeClientErrorKind.Timeout,
                    "realtime_timeout",
                    "The realtime operation timed out."));
            }

            if (stopTransportAfterPoll)
            {
                StopTransport();
            }

            if (pendingUnexpectedDisconnect != null)
            {
                var error = pendingUnexpectedDisconnect;
                pendingUnexpectedDisconnect = null;
                UnexpectedlyDisconnected?.Invoke(error);
            }
        }

        private void OnDestroy()
        {
            StopTransport();
        }

        private void OnApplicationQuit()
        {
            StopTransport();
        }

        public IEnumerator Join(
            JoinShardResponse placement,
            Action<ActiveSimulationSessionResponse> onSuccess,
            Action<RealtimeClientError> onError)
        {
            if (State != RealtimeConnectionState.Disconnected)
            {
                var error = new RealtimeClientError(
                    RealtimeClientErrorKind.Busy,
                    "realtime_busy",
                    "Another realtime connection is already active.");
                LogFailure(error);
                onError(error);
                yield break;
            }

            if (!IsValidPlacement(placement))
            {
                var error = new RealtimeClientError(
                    RealtimeClientErrorKind.Validation,
                    "invalid_join_parameters",
                    "A valid shard placement, simulation endpoint, and join ticket are required.");
                LogFailure(error);
                onError(error);
                yield break;
            }

            if (!DateTime.TryParse(
                    placement.expiresAt,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var expiresAt)
                || expiresAt.ToUniversalTime() <= DateTime.UtcNow)
            {
                var error = new RealtimeClientError(
                    RealtimeClientErrorKind.Validation,
                    "join_ticket_expired",
                    "The shard placement join ticket has expired.");
                LogFailure(error);
                onError(error);
                yield break;
            }

            if (placement.endpoint.protocolVersion != RealtimeProtocol.Version)
            {
                var error = new RealtimeClientError(
                    RealtimeClientErrorKind.Validation,
                    "incompatible_protocol",
                    "The assigned SimulationWorker uses realtime protocol version "
                    + placement.endpoint.protocolVersion + ", but this client requires version "
                    + RealtimeProtocol.Version + ".");
                LogFailure(error);
                onError(error);
                yield break;
            }

            if (!string.Equals(
                    placement.endpoint.simulationRevision,
                    GameSimulationCompatibility.Revision,
                    StringComparison.Ordinal))
            {
                var error = new RealtimeClientError(
                    RealtimeClientErrorKind.Validation,
                    "incompatible_simulation",
                    "The assigned SimulationWorker uses simulation revision '"
                    + placement.endpoint.simulationRevision + "', but this client requires '"
                    + GameSimulationCompatibility.Revision + "'.");
                LogFailure(error);
                onError(error);
                yield break;
            }

            ResetOperation();
            pendingPlacement = placement;
            ConnectedHost = placement.endpoint.host.Trim();
            ConnectedPort = placement.endpoint.udpPort;
            pendingJoinTicket = placement.joinTicket.Trim();
            client.DisconnectTimeout = ShooterMmoClientSession.RealtimeTimeoutSeconds * 1000;
            ClientLog.Info(
                ClientLogCategory.Client,
                "Opening UDP connection to SimulationWorker '" + placement.endpoint.workerId
                + "' runtime '" + placement.endpoint.runtimeId + "' at " + ConnectedHost + ":"
                + ConnectedPort + "/udp for shard '" + placement.shard.id + "'.");

            RealtimeClientError connectionStartError = null;
            try
            {
                if (!client.Start())
                {
                    connectionStartError = new RealtimeClientError(
                        RealtimeClientErrorKind.Network,
                        "udp_start_failed",
                        "The UDP client could not start.");
                }
                else
                {
                    transportStarted = true;
                    State = RealtimeConnectionState.Connecting;
                    operationDeadline = Time.realtimeSinceStartup + ShooterMmoClientSession.RealtimeTimeoutSeconds;
                    client.Connect(ConnectedHost, ConnectedPort, RealtimeProtocol.ConnectionKey);
                }
            }
            catch (Exception)
            {
                State = RealtimeConnectionState.Disconnected;
                StopTransport();
                connectionStartError = new RealtimeClientError(
                    RealtimeClientErrorKind.Network,
                    "udp_connect_failed",
                    "The UDP client could not connect to the assigned SimulationWorker endpoint.");
            }

            if (connectionStartError != null)
            {
                StopTransport();
                LogFailure(connectionStartError);
                onError(connectionStartError);
                yield break;
            }

            while (State == RealtimeConnectionState.Connecting
                   || State == RealtimeConnectionState.Joining)
            {
                yield return null;
            }

            if (State == RealtimeConnectionState.Joined && joinedSession != null)
            {
                onSuccess(joinedSession);
                yield break;
            }

            var joinError = operationError ?? new RealtimeClientError(
                RealtimeClientErrorKind.Network,
                "connection_closed",
                "The realtime connection closed before the join completed.");
            if (operationError == null)
            {
                LogFailure(joinError);
            }

            onError(joinError);
        }

        public IEnumerator Leave(
            string simulationSessionId,
            Action onSuccess,
            Action<RealtimeClientError> onError)
        {
            if (State != RealtimeConnectionState.Joined || serverPeer == null)
            {
                var error = new RealtimeClientError(
                    RealtimeClientErrorKind.Validation,
                    "not_in_simulation",
                    "No active realtime simulation connection exists.");
                LogFailure(error);
                onError(error);
                yield break;
            }

            if (string.IsNullOrWhiteSpace(simulationSessionId))
            {
                var error = new RealtimeClientError(
                    RealtimeClientErrorKind.Validation,
                    "missing_simulation_session",
                    "Simulation session id is required.");
                LogFailure(error);
                onError(error);
                yield break;
            }

            operationError = null;
            leaveAccepted = false;
            State = RealtimeConnectionState.Leaving;
            operationDeadline = Time.realtimeSinceStartup + ShooterMmoClientSession.RealtimeTimeoutSeconds;
            ClientLog.Info(
                ClientLogCategory.Client,
                "Requesting a graceful leave for simulation session '" + simulationSessionId.Trim() + "'.");
            serverPeer.Send(
                RealtimeProtocol.EncodeLeaveRequest(simulationSessionId.Trim()),
                DeliveryMethod.ReliableOrdered);

            while (State == RealtimeConnectionState.Leaving)
            {
                yield return null;
            }

            if (leaveAccepted)
            {
                onSuccess();
                yield break;
            }

            var leaveError = operationError ?? new RealtimeClientError(
                RealtimeClientErrorKind.Network,
                "connection_closed",
                "The realtime connection closed before leave completed.");
            if (operationError == null)
            {
                LogFailure(leaveError);
            }

            onError(leaveError);
        }

        public bool TrySendMovementInputs(PlayerMovementInput[] inputs)
        {
            if (State != RealtimeConnectionState.Joined
                || serverPeer == null
                || inputs == null
                || inputs.Length == 0
                || inputs.Length > RealtimeProtocol.MaximumInputBatchSize)
            {
                return false;
            }

            var protocolInputs = new RealtimeMovementInput[inputs.Length];
            for (var index = 0; index < inputs.Length; index++)
            {
                var input = inputs[index];
                protocolInputs[index] = new RealtimeMovementInput(
                    input.InputSequence,
                    input.ClientTick,
                    input.MoveX,
                    input.MoveY,
                    input.CameraYawDegrees,
                    ToRealtimeButtons(input.Buttons));
            }

            try
            {
                serverPeer.Send(
                    RealtimeProtocol.EncodeMovementInputBatch(protocolInputs),
                    RealtimeProtocol.MovementInputChannel,
                    DeliveryMethod.Sequenced);
                return true;
            }
            catch (ArgumentException exception)
            {
                ClientLog.Error(
                    ClientLogCategory.Client,
                    "Movement input could not be encoded: " + exception.Message);
                return false;
            }
        }

        public void Abort()
        {
            State = RealtimeConnectionState.Disconnected;
            joinedSession = null;
            pendingPlacement = null;
            MovementSession = null;
            LatestServerTick = 0;
            ClearSpawnedEntities("client_aborted");
            operationError = null;
            pendingUnexpectedDisconnect = null;
            StopTransport();
        }

        private void OnPeerConnected(NetPeer peer)
        {
            if (State != RealtimeConnectionState.Connecting)
            {
                return;
            }

            serverPeer = peer;
            State = RealtimeConnectionState.Joining;
            ClientLog.Info(
                ClientLogCategory.Client,
                "UDP transport connected to " + ConnectedHost + ":" + ConnectedPort
                + "/udp. Sending the short-lived join ticket to SimulationWorker.");
            peer.Send(
                RealtimeProtocol.EncodeJoinRequest(pendingJoinTicket),
                DeliveryMethod.ReliableOrdered);
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            serverPeer = null;
            if (State == RealtimeConnectionState.Disconnected)
            {
                return;
            }

            var previousState = State;
            var error = new RealtimeClientError(
                RealtimeClientErrorKind.Network,
                "connection_lost",
                "The realtime connection closed: " + disconnectInfo.Reason + ".");
            operationError = operationError ?? error;
            State = RealtimeConnectionState.Disconnected;
            MovementSession = null;
            LatestServerTick = 0;
            ClearSpawnedEntities("connection_lost");
            stopTransportAfterPoll = true;
            LogFailure(error);

            if (previousState == RealtimeConnectionState.Joined)
            {
                pendingUnexpectedDisconnect = error;
            }
        }

        private void OnNetworkReceive(
            NetPeer peer,
            NetPacketReader reader,
            byte channel,
            DeliveryMethod deliveryMethod)
        {
            try
            {
                var packet = reader.GetRemainingBytes();
                if (!RealtimeProtocol.TryReadMessageType(packet, out var messageType))
                {
                    FailProtocol("invalid_packet", "SimulationWorker returned an invalid realtime packet.");
                    return;
                }

                if (messageType == RealtimeMessageType.SimulationSnapshot)
                {
                    if (deliveryMethod != DeliveryMethod.Unreliable)
                    {
                        FailProtocol(
                            "invalid_snapshot_delivery",
                            "Simulation snapshots require unreliable delivery.");
                        return;
                    }

                    if (ShouldIgnoreSimulationSnapshot(State))
                    {
                        return;
                    }

                    HandleSimulationSnapshot(packet);
                    return;
                }

                if (channel != RealtimeProtocol.ControlChannel
                    || deliveryMethod != DeliveryMethod.ReliableOrdered)
                {
                    FailProtocol(
                        "invalid_control_delivery",
                        "SimulationWorker control messages require the reliable ordered control channel.");
                    return;
                }

                switch (messageType)
                {
                    case RealtimeMessageType.JoinAccepted:
                        HandleJoinAccepted(packet);
                        break;
                    case RealtimeMessageType.JoinRejected:
                        HandleJoinRejected(packet);
                        break;
                    case RealtimeMessageType.LeaveAccepted:
                        HandleLeaveAccepted(packet);
                        break;
                    case RealtimeMessageType.LeaveRejected:
                        HandleLeaveRejected(packet);
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
                    default:
                        FailProtocol("unexpected_message", "SimulationWorker returned a message that is invalid for clients.");
                        break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }

        internal static bool ShouldIgnoreSimulationSnapshot(RealtimeConnectionState state)
        {
            return state != RealtimeConnectionState.Joined;
        }

        private void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
            if (State == RealtimeConnectionState.Disconnected)
            {
                return;
            }

            FailCurrentOperation(new RealtimeClientError(
                RealtimeClientErrorKind.Network,
                "udp_socket_error",
                "UDP transport error: " + socketError + "."));
        }

        private void HandleJoinAccepted(byte[] packet)
        {
            if (State != RealtimeConnectionState.Joining)
            {
                FailProtocol("unexpected_join_response", "Join response arrived in the wrong connection state.");
                return;
            }

            if (!RealtimeProtocol.TryDecodeJoinAccepted(packet, out var session, out var error)
                || !IsValidSession(session))
            {
                FailProtocol("invalid_join_response", error);
                return;
            }

            if (!MatchesPendingPlacement(session, pendingPlacement))
            {
                FailProtocol(
                    "placement_mismatch",
                    "SimulationWorker accepted the join with topology or compatibility metadata that does not match the AuthService placement.");
                return;
            }

            if (!NetworkMovementSession.TryCreate(session, out var movementSession, out error))
            {
                FailProtocol("invalid_movement_configuration", error);
                return;
            }

            var placement = pendingPlacement;
            joinedSession = new ActiveSimulationSessionResponse
            {
                simulationSessionId = session.SimulationSessionId,
                accountId = session.AccountId,
                characterId = session.CharacterId,
                characterName = session.CharacterName,
                shardId = session.ShardId,
                worldId = session.WorldId,
                workerId = placement.endpoint.workerId,
                workerRuntimeId = placement.endpoint.runtimeId,
                joinedAt = session.JoinedAt,
                sessionExpiresAt = session.SessionExpiresAt,
                isReconnect = session.IsReconnect
            };
            pendingJoinTicket = string.Empty;
            pendingPlacement = null;
            operationError = null;
            MovementSession = movementSession;
            LatestServerTick = 0;
            State = RealtimeConnectionState.Joined;
            ClientLog.Info(
                ClientLogCategory.Simulation,
                "Account '" + joinedSession.accountId + "' with character '" + joinedSession.characterName
                + "' (" + joinedSession.characterId + ") connected to shard '" + joinedSession.shardId
                + "' for world '" + joinedSession.worldId + "' through worker '" + joinedSession.workerId
                + "' runtime '" + joinedSession.workerRuntimeId + "'. Simulation session '"
                + joinedSession.simulationSessionId + "' controls network entity '"
                + movementSession.ControlledEntityId + "'.");
            ClientLog.Info(
                ClientLogCategory.Client,
                "Server-authoritative movement is active at " + movementSession.Settings.TickRateHz
                + " ticks per second with " + movementSession.SnapshotRateHz
                + " snapshots per second.");
        }

        private void HandleJoinRejected(byte[] packet)
        {
            if (State != RealtimeConnectionState.Joining)
            {
                FailProtocol("unexpected_join_rejection", "Join rejection arrived in the wrong connection state.");
                return;
            }

            if (!RealtimeProtocol.TryDecodeJoinRejected(packet, out var rejection, out var error))
            {
                FailProtocol("invalid_join_rejection", error);
                return;
            }

            FailCurrentOperation(new RealtimeClientError(
                RealtimeClientErrorKind.Server,
                rejection.Code,
                rejection.Message));
        }

        private void HandleSimulationSnapshot(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeSimulationSnapshot(packet, out var snapshot, out var error))
            {
                FailProtocol("invalid_simulation_snapshot", error);
                return;
            }

            if (LatestServerTick == 0
                || snapshot.ServerTick == LatestServerTick
                || MovementSequence.IsNewer(snapshot.ServerTick, LatestServerTick))
            {
                LatestServerTick = snapshot.ServerTick;
                SimulationSnapshotReceived?.Invoke(snapshot);
            }
        }

        private void HandleEntitySpawn(byte[] packet)
        {
            if (State != RealtimeConnectionState.Joined)
            {
                FailProtocol("unexpected_entity_spawn", "Entity spawn arrived before the simulation session was joined.");
                return;
            }

            if (!RealtimeProtocol.TryDecodeEntitySpawn(packet, out var spawn, out var error))
            {
                FailProtocol("invalid_entity_spawn", error);
                return;
            }

            if (spawnedEntities.TryGetValue(spawn.EntityId, out var existing)
                && (!string.Equals(existing.PersistentId, spawn.PersistentId, StringComparison.OrdinalIgnoreCase)
                    || existing.Kind != spawn.Kind
                    || !string.Equals(existing.ArchetypeId, spawn.ArchetypeId, StringComparison.Ordinal)))
            {
                FailProtocol(
                    "entity_id_conflict",
                    "SimulationWorker reused an active network entity id for another entity.");
                return;
            }

            spawnedEntities[spawn.EntityId] = spawn;
            EntitySpawned?.Invoke(spawn);
        }

        private void HandleEntityDespawn(byte[] packet)
        {
            if (State != RealtimeConnectionState.Joined)
            {
                FailProtocol("unexpected_entity_despawn", "Entity despawn arrived outside an active simulation session.");
                return;
            }

            if (!RealtimeProtocol.TryDecodeEntityDespawn(packet, out var despawn, out var error))
            {
                FailProtocol("invalid_entity_despawn", error);
                return;
            }

            spawnedEntities.Remove(despawn.EntityId);
            EntityDespawned?.Invoke(despawn);
        }

        private void HandleLeaveAccepted(byte[] packet)
        {
            if (State != RealtimeConnectionState.Leaving)
            {
                FailProtocol("unexpected_leave_response", "Leave response arrived in the wrong connection state.");
                return;
            }

            if (!RealtimeProtocol.TryDecodeLeaveAccepted(packet, out var error))
            {
                FailProtocol("invalid_leave_response", error);
                return;
            }

            if (joinedSession != null)
            {
                ClientLog.Info(
                    ClientLogCategory.Simulation,
                    "SimulationWorker accepted the leave for character '" + joinedSession.characterName
                    + "' from shard '" + joinedSession.shardId + "'.");
            }

            leaveAccepted = true;
            ClearSpawnedEntities("left_shard");
            joinedSession = null;
            MovementSession = null;
            LatestServerTick = 0;
            State = RealtimeConnectionState.Disconnected;
            stopTransportAfterPoll = true;
        }

        private void HandleLeaveRejected(byte[] packet)
        {
            if (State != RealtimeConnectionState.Leaving)
            {
                FailProtocol("unexpected_leave_rejection", "Leave rejection arrived in the wrong connection state.");
                return;
            }

            if (!RealtimeProtocol.TryDecodeLeaveRejected(packet, out var rejection, out var error))
            {
                FailProtocol("invalid_leave_rejection", error);
                return;
            }

            operationError = new RealtimeClientError(
                RealtimeClientErrorKind.Server,
                rejection.Code,
                rejection.Message);
            LogFailure(operationError);
            State = RealtimeConnectionState.Joined;
        }

        private void HandleServerDisconnect(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeServerDisconnect(packet, out var reason, out var error))
            {
                FailProtocol("invalid_disconnect_reason", error);
                return;
            }

            var previousState = State;
            var disconnectError = new RealtimeClientError(
                RealtimeClientErrorKind.Server,
                reason.Code,
                reason.Message);
            operationError = disconnectError;
            ClearSpawnedEntities(reason.Code);
            joinedSession = null;
            MovementSession = null;
            LatestServerTick = 0;
            State = RealtimeConnectionState.Disconnected;
            stopTransportAfterPoll = true;
            LogFailure(disconnectError);

            if (previousState == RealtimeConnectionState.Joined)
            {
                pendingUnexpectedDisconnect = disconnectError;
            }
        }

        private void FailProtocol(string code, string message)
        {
            FailCurrentOperation(new RealtimeClientError(
                RealtimeClientErrorKind.Protocol,
                code,
                string.IsNullOrWhiteSpace(message)
                    ? "The realtime protocol response was invalid."
                    : message));
        }

        private void FailCurrentOperation(RealtimeClientError error)
        {
            var previousState = State;
            operationError = error;
            ClearSpawnedEntities(error.Code);
            joinedSession = null;
            MovementSession = null;
            LatestServerTick = 0;
            State = RealtimeConnectionState.Disconnected;
            stopTransportAfterPoll = true;
            LogFailure(error);

            if (previousState == RealtimeConnectionState.Joined)
            {
                pendingUnexpectedDisconnect = error;
            }
        }

        private void ResetOperation()
        {
            operationError = null;
            ClearSpawnedEntities("session_reset");
            joinedSession = null;
            MovementSession = null;
            LatestServerTick = 0;
            leaveAccepted = false;
            stopTransportAfterPoll = false;
            pendingUnexpectedDisconnect = null;
            pendingJoinTicket = string.Empty;
            pendingPlacement = null;
        }

        private void StopTransport()
        {
            stopTransportAfterPoll = false;
            if (transportStarted)
            {
                client.Stop();
                transportStarted = false;
            }

            serverPeer = null;
            ConnectedHost = string.Empty;
            ConnectedPort = 0;
            pendingJoinTicket = string.Empty;
            pendingPlacement = null;
        }

        private void ClearSpawnedEntities(string reason)
        {
            if (spawnedEntities.Count == 0)
            {
                return;
            }

            var entityIds = new List<ulong>(spawnedEntities.Keys);
            spawnedEntities.Clear();
            for (var index = 0; index < entityIds.Count; index++)
            {
                EntityDespawned?.Invoke(new RealtimeEntityDespawn(entityIds[index], reason));
            }
        }

        private static bool IsValidSession(RealtimeJoinAccepted session)
        {
            if (session == null)
            {
                return false;
            }

            return Guid.TryParse(session.SimulationSessionId, out _)
                && Guid.TryParse(session.AccountId, out _)
                && Guid.TryParse(session.CharacterId, out _)
                && session.ControlledEntityId != 0
                && !string.IsNullOrWhiteSpace(session.CharacterName)
                && !string.IsNullOrWhiteSpace(session.ShardId)
                && !string.IsNullOrWhiteSpace(session.WorldId)
                && !string.IsNullOrWhiteSpace(session.SimulationRevision)
                && !string.IsNullOrWhiteSpace(session.CollisionRevision)
                && DateTime.TryParse(session.JoinedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out _)
                && DateTime.TryParse(
                    session.SessionExpiresAt,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out _);
        }

        private static bool IsValidPlacement(JoinShardResponse placement)
        {
            return placement != null
                && placement.shard != null
                && placement.endpoint != null
                && !string.IsNullOrWhiteSpace(placement.shard.id)
                && !string.IsNullOrWhiteSpace(placement.shard.worldId)
                && !string.IsNullOrWhiteSpace(placement.endpoint.workerId)
                && !string.IsNullOrWhiteSpace(placement.endpoint.runtimeId)
                && !string.IsNullOrWhiteSpace(placement.endpoint.host)
                && placement.endpoint.udpPort > 0
                && placement.endpoint.udpPort <= 65535
                && !string.IsNullOrWhiteSpace(placement.endpoint.simulationRevision)
                && !string.IsNullOrWhiteSpace(placement.endpoint.collisionRevision)
                && Guid.TryParse(placement.characterId, out _)
                && !string.IsNullOrWhiteSpace(placement.joinTicket)
                && !string.IsNullOrWhiteSpace(placement.expiresAt);
        }

        private static bool MatchesPendingPlacement(
            RealtimeJoinAccepted session,
            JoinShardResponse placement)
        {
            return IsValidPlacement(placement)
                && string.Equals(session.CharacterId, placement.characterId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(session.ShardId, placement.shard.id, StringComparison.Ordinal)
                && string.Equals(session.WorldId, placement.shard.worldId, StringComparison.Ordinal)
                && string.Equals(
                    session.SimulationRevision,
                    placement.endpoint.simulationRevision,
                    StringComparison.Ordinal)
                && string.Equals(
                    session.CollisionRevision,
                    placement.endpoint.collisionRevision,
                    StringComparison.Ordinal);
        }

        private static void LogFailure(RealtimeClientError error)
        {
            var category = error.Kind == RealtimeClientErrorKind.Server
                ? ClientLogCategory.Simulation
                : ClientLogCategory.Client;
            ClientLog.Error(
                category,
                "Realtime operation failed: " + error.ToDisplayMessage());
        }

        private static RealtimeMovementButtons ToRealtimeButtons(PlayerMovementButtons buttons)
        {
            var result = RealtimeMovementButtons.None;
            if ((buttons & PlayerMovementButtons.Sprint) != 0)
            {
                result |= RealtimeMovementButtons.Sprint;
            }

            if ((buttons & PlayerMovementButtons.Jump) != 0)
            {
                result |= RealtimeMovementButtons.Jump;
            }

            if ((buttons & PlayerMovementButtons.Aim) != 0)
            {
                result |= RealtimeMovementButtons.Aim;
            }

            return result;
        }
    }
}
