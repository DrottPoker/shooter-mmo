using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using LiteNetLib;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;

namespace Mmorpg.Client.Networking
{
    public enum RealtimeConnectionState
    {
        Disconnected,
        Connecting,
        Joining,
        Joined,
        Leaving
    }

    // What features read of the realtime session, and the two ways a feature ends it.
    public interface IRealtimeSession
    {
        // Raised when the worker accepts a join, before any later message of that session is
        // handled, so session-scoped client state starts empty.
        event Action SessionJoined;

        // Raised once whenever a joined session ends for any reason, including a graceful
        // leave, so session-scoped client state can reset. The error is null for a leave and
        // an abort.
        event Action<RealtimeClientError> SessionEnded;

        // Raised before SessionEnded when a joined session ends without a leave or an abort.
        event Action<RealtimeClientError> UnexpectedlyDisconnected;

        RealtimeConnectionState State { get; }

        bool IsJoined { get; }

        // The session the worker accepted, while it is joined or leaving; otherwise null.
        ActiveSimulationSessionResponse JoinedSession { get; }

        // The gameplay rules of the joined Shard, or null outside a joined session.
        RealtimeShardRules ShardRules { get; }

        // Ends the session as a protocol failure that the client found, such as state that
        // conflicts with what the worker sent.
        void DisconnectForClientFailure(string code, string message);

        // Closes the connection at once, without a leave.
        void Abort();
    }

    // A part that keeps state of one joined session. The session clears it wherever it drops
    // the session: when a join starts or is accepted, and at every ending.
    internal interface IRealtimeSessionScoped
    {
        void ClearSessionState();
    }

    // The connection state machine of the hub: join, leave, abort, the deadlines, every way a
    // session ends, and the events that announce it. It owns the joined session, the Shard
    // rules, and the movement session, clears the presence and the session-scoped parts at
    // the point each ending requires, and handles the five session messages.
    public sealed class RealtimeSession : IRealtimeSession
    {
        private readonly RealtimeConnection connection;
        private readonly Func<float> realtimeClock;
        private readonly RealtimeDiagnostics diagnostics;
        private readonly RealtimePresenceRegistry presence;
        private readonly List<IRealtimeSessionScoped> scopedParts = new List<IRealtimeSessionScoped>();
        private RealtimeClientError operationError;
        private RealtimeClientError pendingUnexpectedDisconnect;
        private string pendingJoinTicket = string.Empty;
        private JoinShardResponse pendingPlacement;
        private float operationDeadline;
        // A session that ended stops its transport after the poll that found the end.
        private bool stopTransportAfterPoll;
        private bool sessionJoined;
        private bool pendingSessionEnded;
        private RealtimeClientError pendingSessionEndError;
        private bool leaveAccepted;

        internal RealtimeSession(
            RealtimeConnection connection,
            Func<float> realtimeClock,
            RealtimeDiagnostics diagnostics,
            RealtimePresenceRegistry presence)
        {
            this.connection = connection;
            this.realtimeClock = realtimeClock;
            this.diagnostics = diagnostics;
            this.presence = presence;
            scopedParts.Add(diagnostics);
        }

        public event Action SessionJoined;

        public event Action<RealtimeClientError> SessionEnded;

        public event Action<RealtimeClientError> UnexpectedlyDisconnected;

        public RealtimeConnectionState State { get; private set; }

        public bool IsJoined
        {
            get { return State == RealtimeConnectionState.Joined; }
        }

        public ActiveSimulationSessionResponse JoinedSession { get; private set; }

        public RealtimeShardRules ShardRules { get; private set; }

        // Set exactly while the state is Joined or Leaving, so the handlers of those states
        // always have it.
        internal NetworkMovementSession MovementSession { get; private set; }

        internal void Track(IRealtimeSessionScoped part)
        {
            scopedParts.Add(part);
        }

        internal void RegisterMessages(RealtimeMessageTable messages)
        {
            messages.Register(
                RealtimeMessageType.JoinAccepted,
                HandleJoinAccepted,
                RealtimeStates.Joining,
                RealtimeStates.None,
                "unexpected_join_response",
                "Join response arrived in the wrong connection state.");
            messages.Register(
                RealtimeMessageType.JoinRejected,
                HandleJoinRejected,
                RealtimeStates.Joining,
                RealtimeStates.None,
                "unexpected_join_rejection",
                "Join rejection arrived in the wrong connection state.");
            messages.Register(
                RealtimeMessageType.LeaveAccepted,
                HandleLeaveAccepted,
                RealtimeStates.Leaving,
                RealtimeStates.None,
                "unexpected_leave_response",
                "Leave response arrived in the wrong connection state.");
            messages.Register(
                RealtimeMessageType.LeaveRejected,
                HandleLeaveRejected,
                RealtimeStates.Leaving,
                RealtimeStates.None,
                "unexpected_leave_rejection",
                "Leave rejection arrived in the wrong connection state.");
            // The worker may end the connection in any state.
            messages.Register(
                RealtimeMessageType.ServerDisconnect,
                HandleServerDisconnect,
                RealtimeStates.Receiving,
                RealtimeStates.None,
                null,
                null);
        }

        public IEnumerator Join(
            JoinShardResponse placement,
            Action<ActiveSimulationSessionResponse> onSuccess,
            Action<RealtimeClientError> onError)
        {
            // A join right after a failure first finishes the end of that session: its transport
            // stops and its events fire, so the new transport starts on a stopped one.
            FinishPendingEnd();

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
            connection.SetEndpoint(placement.endpoint.host.Trim(), placement.endpoint.udpPort);
            pendingJoinTicket = placement.joinTicket.Trim();
            ClientLog.Info(
                ClientLogCategory.Client,
                "Opening UDP connection to SimulationWorker '" + placement.endpoint.workerId
                + "' runtime '" + placement.endpoint.runtimeId + "' at " + connection.Host + ":"
                + connection.Port + "/udp for shard '" + placement.shard.id + "'.");

            RealtimeClientError connectionStartError = null;
            try
            {
                if (!connection.Start(ClientSession.RealtimeTimeoutSeconds * 1000))
                {
                    connectionStartError = new RealtimeClientError(
                        RealtimeClientErrorKind.Network,
                        "udp_start_failed",
                        "The UDP client could not start.");
                }
                else
                {
                    State = RealtimeConnectionState.Connecting;
                    operationDeadline = realtimeClock() + ClientSession.RealtimeTimeoutSeconds;
                    connection.Connect(RealtimeProtocol.ConnectionKey);
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

            if (State == RealtimeConnectionState.Joined && JoinedSession != null)
            {
                onSuccess(JoinedSession);
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
            if (State != RealtimeConnectionState.Joined || !connection.IsServerConnected)
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
            operationDeadline = realtimeClock() + ClientSession.RealtimeTimeoutSeconds;
            ClientLog.Info(
                ClientLogCategory.Client,
                "Requesting a graceful leave for simulation session '" + simulationSessionId.Trim() + "'.");
            var leavePacket = RealtimeProtocol.EncodeLeaveRequest(simulationSessionId.Trim());
            connection.Send(leavePacket, RealtimeProtocol.ControlChannel, DeliveryMethod.ReliableOrdered);
            diagnostics.RecordSentPacket(leavePacket.Length);

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

        public void Abort()
        {
            // A session that a failure ended before this call ends with the events it owes,
            // at once, and this abort raises no second SessionEnded.
            FinishPendingEnd();

            var endedJoinedSession = sessionJoined;
            sessionJoined = false;
            State = RealtimeConnectionState.Disconnected;
            pendingPlacement = null;
            ReleaseSession();
            presence.Clear("client_aborted");
            operationError = null;
            StopTransport();
            if (endedJoinedSession)
            {
                SessionEnded?.Invoke(null);
            }
        }

        public void DisconnectForClientFailure(string code, string message)
        {
            if (State == RealtimeConnectionState.Disconnected)
            {
                return;
            }

            FailProtocol(code, message);
        }

        internal void FailProtocol(string code, string message)
        {
            FailCurrentOperation(new RealtimeClientError(
                RealtimeClientErrorKind.Protocol,
                code,
                string.IsNullOrWhiteSpace(message)
                    ? "The realtime protocol response was invalid."
                    : message));
        }

        // Runs after the poll of each frame: the deadline of a join or leave, then the end of a
        // session that this frame or a call since the last frame found.
        internal void EndFrame()
        {
            if (State != RealtimeConnectionState.Disconnected
                && State != RealtimeConnectionState.Joined
                && realtimeClock() >= operationDeadline)
            {
                FailCurrentOperation(new RealtimeClientError(
                    RealtimeClientErrorKind.Timeout,
                    "realtime_timeout",
                    "The realtime operation timed out."));
            }

            FinishPendingEnd();
        }

        // Stops the transport when the hub goes away.
        internal void Shutdown()
        {
            StopTransport();
        }

        internal void OnTransportConnected()
        {
            if (State != RealtimeConnectionState.Connecting)
            {
                return;
            }

            connection.MarkServerConnected();
            State = RealtimeConnectionState.Joining;
            ClientLog.Info(
                ClientLogCategory.Client,
                "UDP transport connected to " + connection.Host + ":" + connection.Port
                + "/udp. Sending the short-lived join ticket to SimulationWorker.");
            var joinPacket = RealtimeProtocol.EncodeJoinRequest(pendingJoinTicket);
            connection.Send(joinPacket, RealtimeProtocol.ControlChannel, DeliveryMethod.ReliableOrdered);
            diagnostics.RecordSentPacket(joinPacket.Length);
        }

        internal void OnTransportDisconnected(DisconnectReason reason)
        {
            connection.MarkServerDisconnected();
            if (State == RealtimeConnectionState.Disconnected)
            {
                return;
            }

            var previousState = State;
            var error = new RealtimeClientError(
                RealtimeClientErrorKind.Network,
                "connection_lost",
                "The realtime connection closed: " + reason + ".");
            operationError = error;
            State = RealtimeConnectionState.Disconnected;
            ReleaseSession();
            presence.Clear("connection_lost");
            stopTransportAfterPoll = true;
            MarkSessionEnded(error);
            LogFailure(error);

            if (previousState == RealtimeConnectionState.Joined)
            {
                pendingUnexpectedDisconnect = error;
            }
        }

        internal void OnTransportError(SocketError socketError)
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
            if (!RealtimeProtocol.TryDecodeJoinAccepted(packet, out var accepted, out var error)
                || !IsValidSession(accepted))
            {
                FailProtocol("invalid_join_response", error);
                return;
            }

            if (!MatchesPendingPlacement(accepted, pendingPlacement))
            {
                FailProtocol(
                    "placement_mismatch",
                    "SimulationWorker accepted the join with topology or compatibility metadata that does not match the GameService placement.");
                return;
            }

            if (!NetworkMovementSession.TryCreate(accepted, out var movementSession, out error))
            {
                FailProtocol("invalid_movement_configuration", error);
                return;
            }

            var placement = pendingPlacement;
            var joined = new ActiveSimulationSessionResponse
            {
                simulationSessionId = accepted.SimulationSessionId,
                accountId = accepted.AccountId,
                characterId = accepted.CharacterId,
                characterName = accepted.CharacterName,
                shardId = accepted.ShardId,
                worldId = accepted.WorldId,
                workerId = placement.endpoint.workerId,
                workerRuntimeId = placement.endpoint.runtimeId,
                joinedAt = accepted.JoinedAt,
                sessionExpiresAt = accepted.SessionExpiresAt,
                isReconnect = accepted.IsReconnect
            };
            JoinedSession = joined;
            pendingJoinTicket = string.Empty;
            pendingPlacement = null;
            operationError = null;
            MovementSession = movementSession;
            ShardRules = accepted.ShardRules;
            ClearScopedParts();
            State = RealtimeConnectionState.Joined;
            sessionJoined = true;
            ClientLog.Info(
                ClientLogCategory.Simulation,
                "Account '" + joined.accountId + "' with character '" + joined.characterName
                + "' (" + joined.characterId + ") connected to shard '" + joined.shardId
                + "' for world '" + joined.worldId + "' through worker '" + joined.workerId
                + "' runtime '" + joined.workerRuntimeId + "'. Simulation session '"
                + joined.simulationSessionId + "' controls network entity '"
                + movementSession.ControlledEntityId + "'.");
            ClientLog.Info(
                ClientLogCategory.Client,
                "Server-authoritative movement is active at " + movementSession.Settings.TickRateHz
                + " ticks per second with " + movementSession.SnapshotRateHz
                + " snapshots per second. Player versus player is "
                + (ShardRules.PlayerVersusPlayer ? "allowed" : "disabled") + " on this Shard.");
            SessionJoined?.Invoke();
        }

        private void HandleJoinRejected(byte[] packet)
        {
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

        // The presence is cleared while the state is still Leaving, with the movement session.
        private void HandleLeaveAccepted(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeLeaveAccepted(packet, out _, out var error))
            {
                FailProtocol("invalid_leave_response", error);
                return;
            }

            if (JoinedSession != null)
            {
                ClientLog.Info(
                    ClientLogCategory.Simulation,
                    "SimulationWorker accepted the leave for character '" + JoinedSession.characterName
                    + "' from shard '" + JoinedSession.shardId + "'.");
            }

            leaveAccepted = true;
            presence.Clear("left_shard");
            ReleaseSession();
            State = RealtimeConnectionState.Disconnected;
            stopTransportAfterPoll = true;
            MarkSessionEnded(null);
        }

        private void HandleLeaveRejected(byte[] packet)
        {
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
            presence.Clear(reason.Code);
            ReleaseSession();
            State = RealtimeConnectionState.Disconnected;
            stopTransportAfterPoll = true;
            MarkSessionEnded(disconnectError);
            LogFailure(disconnectError);

            if (previousState == RealtimeConnectionState.Joined)
            {
                pendingUnexpectedDisconnect = disconnectError;
            }
        }

        // The presence is cleared before the state changes, so its despawns still see the
        // state and the movement session that the failure ended.
        private void FailCurrentOperation(RealtimeClientError error)
        {
            var previousState = State;
            operationError = error;
            presence.Clear(error.Code);
            ReleaseSession();
            State = RealtimeConnectionState.Disconnected;
            stopTransportAfterPoll = true;
            MarkSessionEnded(error);
            LogFailure(error);

            if (previousState == RealtimeConnectionState.Joined)
            {
                pendingUnexpectedDisconnect = error;
            }
        }

        private void MarkSessionEnded(RealtimeClientError error)
        {
            if (!sessionJoined)
            {
                return;
            }

            sessionJoined = false;
            pendingSessionEnded = true;
            pendingSessionEndError = error;
        }

        // Stops the transport of a session that ended, then raises what that end owes:
        // UnexpectedlyDisconnected for an unexpected end, then SessionEnded. Each is taken
        // before it is raised, so a listener that joins again or aborts never sees it twice.
        private void FinishPendingEnd()
        {
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

            if (pendingSessionEnded)
            {
                var error = pendingSessionEndError;
                pendingSessionEnded = false;
                pendingSessionEndError = null;
                SessionEnded?.Invoke(error);
            }
        }

        // A join starts after FinishPendingEnd, so no end of an earlier session is pending.
        private void ResetOperation()
        {
            operationError = null;
            presence.Clear("session_reset");
            ReleaseSession();
            leaveAccepted = false;
            pendingJoinTicket = string.Empty;
            pendingPlacement = null;
        }

        // Drops what belongs to the joined session; each ending clears the presence on its own,
        // before or after its state change.
        private void ReleaseSession()
        {
            JoinedSession = null;
            MovementSession = null;
            ShardRules = null;
            ClearScopedParts();
        }

        private void ClearScopedParts()
        {
            for (var index = 0; index < scopedParts.Count; index++)
            {
                scopedParts[index].ClearSessionState();
            }
        }

        private void StopTransport()
        {
            stopTransportAfterPoll = false;
            connection.Stop();
            pendingJoinTicket = string.Empty;
            pendingPlacement = null;
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
                "Realtime operation failed: " + error.ToDiagnostic());
        }
    }
}
