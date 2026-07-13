using System;
using System.Collections;
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

    public sealed class RealtimeWorldClient : MonoBehaviour
    {
        private EventBasedNetListener listener;
        private NetManager client;
        private NetPeer serverPeer;
        private RealtimeClientError operationError;
        private ActivePlayerSessionResponse joinedSession;
        private RealtimeClientError pendingUnexpectedDisconnect;
        private string pendingJoinTicket;
        private float operationDeadline;
        private bool transportStarted;
        private bool stopTransportAfterPoll;
        private bool leaveAccepted;

        public event Action<RealtimeClientError> UnexpectedlyDisconnected;

        public event Action<RealtimeWorldSnapshot> WorldSnapshotReceived;

        public RealtimeConnectionState State { get; private set; }

        public string ConnectedHost { get; private set; } = string.Empty;

        public int ConnectedPort { get; private set; }

        public NetworkMovementSession MovementSession { get; private set; }

        public uint LatestServerTick { get; private set; }

        public bool IsJoined
        {
            get { return State == RealtimeConnectionState.Joined; }
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
            string host,
            int port,
            string joinTicket,
            Action<ActivePlayerSessionResponse> onSuccess,
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

            if (string.IsNullOrWhiteSpace(host)
                || port <= 0
                || port > 65535
                || string.IsNullOrWhiteSpace(joinTicket))
            {
                var error = new RealtimeClientError(
                    RealtimeClientErrorKind.Validation,
                    "invalid_join_parameters",
                    "World host, UDP port, and join ticket are required.");
                LogFailure(error);
                onError(error);
                yield break;
            }

            ResetOperation();
            ConnectedHost = host.Trim();
            ConnectedPort = port;
            pendingJoinTicket = joinTicket.Trim();
            client.DisconnectTimeout = ShooterMmoClientSession.RealtimeTimeoutSeconds * 1000;
            ClientLog.Info(
                ClientLogCategory.Client,
                "Opening UDP connection to " + ConnectedHost + ":" + ConnectedPort + "/udp.");

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
                    "The UDP client could not connect to the selected world endpoint.");
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
            string worldSessionId,
            Action onSuccess,
            Action<RealtimeClientError> onError)
        {
            if (State != RealtimeConnectionState.Joined || serverPeer == null)
            {
                var error = new RealtimeClientError(
                    RealtimeClientErrorKind.Validation,
                    "not_in_world",
                    "No active realtime world connection exists.");
                LogFailure(error);
                onError(error);
                yield break;
            }

            if (string.IsNullOrWhiteSpace(worldSessionId))
            {
                var error = new RealtimeClientError(
                    RealtimeClientErrorKind.Validation,
                    "missing_world_session",
                    "World session id is required.");
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
                "Requesting a graceful leave for world session '" + worldSessionId.Trim() + "'.");
            serverPeer.Send(
                RealtimeProtocol.EncodeLeaveRequest(worldSessionId.Trim()),
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
            MovementSession = null;
            LatestServerTick = 0;
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
                + "/udp. Sending the short-lived join ticket to WorldServer.");
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
                    FailProtocol("invalid_packet", "WorldServer returned an invalid realtime packet.");
                    return;
                }

                if (messageType == RealtimeMessageType.WorldSnapshot)
                {
                    if (deliveryMethod != DeliveryMethod.Unreliable)
                    {
                        FailProtocol(
                            "invalid_snapshot_delivery",
                            "World snapshots require unreliable delivery.");
                        return;
                    }

                    if (ShouldIgnoreWorldSnapshot(State))
                    {
                        return;
                    }

                    HandleWorldSnapshot(packet);
                    return;
                }

                if (channel != RealtimeProtocol.ControlChannel
                    || deliveryMethod != DeliveryMethod.ReliableOrdered)
                {
                    FailProtocol(
                        "invalid_control_delivery",
                        "WorldServer control messages require the reliable ordered control channel.");
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
                    default:
                        FailProtocol("unexpected_message", "WorldServer returned a message that is invalid for clients.");
                        break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }

        internal static bool ShouldIgnoreWorldSnapshot(RealtimeConnectionState state)
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

            if (!NetworkMovementSession.TryCreate(session, out var movementSession, out error))
            {
                FailProtocol("invalid_movement_configuration", error);
                return;
            }

            joinedSession = new ActivePlayerSessionResponse
            {
                worldSessionId = session.WorldSessionId,
                accountId = session.AccountId,
                characterId = session.CharacterId,
                characterName = session.CharacterName,
                worldId = session.WorldId,
                joinedAt = session.JoinedAt,
                sessionExpiresAt = session.SessionExpiresAt,
                isReconnect = session.IsReconnect
            };
            pendingJoinTicket = string.Empty;
            operationError = null;
            MovementSession = movementSession;
            LatestServerTick = 0;
            State = RealtimeConnectionState.Joined;
            ClientLog.Info(
                ClientLogCategory.WorldServer,
                "Account '" + joinedSession.accountId + "' with character '" + joinedSession.characterName
                + "' (" + joinedSession.characterId + ") connected to world '" + joinedSession.worldId
                + "'. World session '" + joinedSession.worldSessionId + "' is active.");
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

        private void HandleWorldSnapshot(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeWorldSnapshot(packet, out var snapshot, out var error))
            {
                FailProtocol("invalid_world_snapshot", error);
                return;
            }

            if (LatestServerTick == 0
                || snapshot.ServerTick == LatestServerTick
                || MovementSequence.IsNewer(snapshot.ServerTick, LatestServerTick))
            {
                LatestServerTick = snapshot.ServerTick;
                WorldSnapshotReceived?.Invoke(snapshot);
            }
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
                    ClientLogCategory.WorldServer,
                    "WorldServer accepted the leave for character '" + joinedSession.characterName
                    + "' from world '" + joinedSession.worldId + "'.");
            }

            leaveAccepted = true;
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
            joinedSession = null;
            MovementSession = null;
            LatestServerTick = 0;
            leaveAccepted = false;
            stopTransportAfterPoll = false;
            pendingUnexpectedDisconnect = null;
            pendingJoinTicket = string.Empty;
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
        }

        private static bool IsValidSession(RealtimeJoinAccepted session)
        {
            if (session == null)
            {
                return false;
            }

            return Guid.TryParse(session.WorldSessionId, out _)
                && Guid.TryParse(session.AccountId, out _)
                && Guid.TryParse(session.CharacterId, out _)
                && !string.IsNullOrWhiteSpace(session.CharacterName)
                && !string.IsNullOrWhiteSpace(session.WorldId)
                && !string.IsNullOrWhiteSpace(session.CollisionRevision)
                && DateTime.TryParse(session.JoinedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out _)
                && DateTime.TryParse(
                    session.SessionExpiresAt,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out _);
        }

        private static void LogFailure(RealtimeClientError error)
        {
            var category = error.Kind == RealtimeClientErrorKind.Server
                ? ClientLogCategory.WorldServer
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
