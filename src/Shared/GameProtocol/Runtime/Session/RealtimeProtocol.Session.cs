using System;

namespace Mmorpg.GameProtocol
{
    // Join, leave, and disconnect messages of a realtime session.
    public static partial class RealtimeProtocol
    {
        private const int MaximumJoinTicketLength = 1024;

        public static byte[] EncodeJoinRequest(string joinTicket)
        {
            return Encode(RealtimeMessageType.JoinRequest, writer =>
            {
                WriteString(writer, joinTicket, MaximumJoinTicketLength, nameof(joinTicket));
            });
        }

        public static bool TryDecodeJoinRequest(byte[] data, out string joinTicket, out string error)
        {
            joinTicket = string.Empty;
            if (!TryCreateReader(data, RealtimeMessageType.JoinRequest, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadString(reader, MaximumJoinTicketLength, out joinTicket, out error))
                {
                    return false;
                }

                return TryFinish(stream, out error);
            }
        }

        public static byte[] EncodeJoinAccepted(RealtimeJoinAccepted session)
        {
            return Encode(RealtimeMessageType.JoinAccepted, writer =>
            {
                WriteString(writer, session.SimulationSessionId, MaximumIdentifierLength, nameof(session.SimulationSessionId));
                WriteString(writer, session.AccountId, MaximumIdentifierLength, nameof(session.AccountId));
                WriteString(writer, session.CharacterId, MaximumIdentifierLength, nameof(session.CharacterId));
                WriteString(writer, session.CharacterName, MaximumNameLength, nameof(session.CharacterName));
                WriteString(writer, session.ShardId, MaximumIdentifierLength, nameof(session.ShardId));
                WriteString(writer, session.WorldId, MaximumIdentifierLength, nameof(session.WorldId));
                if (session.ControlledEntityId == 0)
                {
                    throw new ArgumentException("Controlled entity id is invalid.", nameof(session));
                }

                writer.Write(session.ControlledEntityId);
                WriteString(
                    writer,
                    session.SimulationRevision,
                    MaximumIdentifierLength,
                    nameof(session.SimulationRevision));
                WriteString(
                    writer,
                    session.CollisionRevision,
                    MaximumIdentifierLength,
                    nameof(session.CollisionRevision));
                WriteString(writer, session.JoinedAt, MaximumTimestampLength, nameof(session.JoinedAt));
                WriteString(writer, session.SessionExpiresAt, MaximumTimestampLength, nameof(session.SessionExpiresAt));
                writer.Write(session.IsReconnect);
                WriteCarryState(writer, session.CarryState);
                WriteMovementSettings(writer, session.MovementSettings);
                WritePlayerState(writer, session.InitialPlayerState);
            });
        }

        public static bool TryDecodeJoinAccepted(
            byte[] data,
            out RealtimeJoinAccepted session,
            out string error)
        {
            session = null;
            if (!TryCreateReader(data, RealtimeMessageType.JoinAccepted, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadString(reader, MaximumIdentifierLength, out var simulationSessionId, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var accountId, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var characterId, out error)
                    || !TryReadString(reader, MaximumNameLength, out var characterName, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var shardId, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var worldId, out error)
                    || !TryReadUInt64(reader, out var controlledEntityId, out error)
                    || !TryReadString(
                        reader,
                        MaximumIdentifierLength,
                        out var simulationRevision,
                        out error)
                    || !TryReadString(
                        reader,
                        MaximumIdentifierLength,
                        out var collisionRevision,
                        out error)
                    || !TryReadString(reader, MaximumTimestampLength, out var joinedAt, out error)
                    || !TryReadString(reader, MaximumTimestampLength, out var sessionExpiresAt, out error))
                {
                    return false;
                }

                if (controlledEntityId == 0)
                {
                    error = "Controlled entity id is invalid.";
                    return false;
                }

                if (!TryReadBoolean(reader, out var isReconnect, out error)
                    || !TryReadCarryState(reader, out var carryState, out error)
                    || !TryReadMovementSettings(reader, out var movementSettings, out error)
                    || !TryReadPlayerState(reader, out var initialPlayerState, out error)
                    || !TryFinish(stream, out error))
                {
                    return false;
                }

                session = new RealtimeJoinAccepted(
                    simulationSessionId,
                    accountId,
                    characterId,
                    characterName,
                    shardId,
                    worldId,
                    controlledEntityId,
                    simulationRevision,
                    collisionRevision,
                    joinedAt,
                    sessionExpiresAt,
                    isReconnect,
                    carryState,
                    movementSettings,
                    initialPlayerState);
                return true;
            }
        }

        public static byte[] EncodeJoinRejected(string code, string message)
        {
            return EncodeError(RealtimeMessageType.JoinRejected, code, message);
        }

        public static bool TryDecodeJoinRejected(byte[] data, out RealtimeError rejection, out string error)
        {
            return TryDecodeError(data, RealtimeMessageType.JoinRejected, out rejection, out error);
        }

        public static byte[] EncodeLeaveRequest(string simulationSessionId)
        {
            return Encode(RealtimeMessageType.LeaveRequest, writer =>
            {
                WriteString(writer, simulationSessionId, MaximumIdentifierLength, nameof(simulationSessionId));
            });
        }

        public static bool TryDecodeLeaveRequest(byte[] data, out string simulationSessionId, out string error)
        {
            simulationSessionId = string.Empty;
            if (!TryCreateReader(data, RealtimeMessageType.LeaveRequest, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadString(reader, MaximumIdentifierLength, out simulationSessionId, out error))
                {
                    return false;
                }

                return TryFinish(stream, out error);
            }
        }

        public static byte[] EncodeLeaveAccepted()
        {
            return Encode(RealtimeMessageType.LeaveAccepted, _ => { });
        }

        public static bool TryDecodeLeaveAccepted(byte[] data, out string error)
        {
            if (!TryCreateReader(data, RealtimeMessageType.LeaveAccepted, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                return TryFinish(stream, out error);
            }
        }

        public static byte[] EncodeLeaveRejected(string code, string message)
        {
            return EncodeError(RealtimeMessageType.LeaveRejected, code, message);
        }

        public static bool TryDecodeLeaveRejected(byte[] data, out RealtimeError rejection, out string error)
        {
            return TryDecodeError(data, RealtimeMessageType.LeaveRejected, out rejection, out error);
        }

        public static byte[] EncodeServerDisconnect(string code, string message)
        {
            return EncodeError(RealtimeMessageType.ServerDisconnect, code, message);
        }

        public static bool TryDecodeServerDisconnect(byte[] data, out RealtimeError reason, out string error)
        {
            return TryDecodeError(data, RealtimeMessageType.ServerDisconnect, out reason, out error);
        }
    }
}
