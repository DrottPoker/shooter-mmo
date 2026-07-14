using System;
using System.IO;
using System.Text;

namespace ShooterMmo.GameProtocol
{
    public enum RealtimeMessageType : byte
    {
        JoinRequest = 1,
        JoinAccepted = 2,
        JoinRejected = 3,
        LeaveRequest = 4,
        LeaveAccepted = 5,
        LeaveRejected = 6,
        ServerDisconnect = 7,
        MovementInputBatch = 8,
        SimulationSnapshot = 9,
        EntitySpawn = 10,
        EntityDespawn = 11
    }

    [Flags]
    public enum RealtimeMovementButtons : byte
    {
        None = 0,
        Sprint = 1,
        Jump = 2,
        Aim = 4
    }

    public enum RealtimeEntityKind : byte
    {
        Player = 1
    }

    public sealed class RealtimeJoinAccepted
    {
        public RealtimeJoinAccepted(
            string simulationSessionId,
            string accountId,
            string characterId,
            string characterName,
            string shardId,
            string worldId,
            ulong controlledEntityId,
            string simulationRevision,
            string collisionRevision,
            string joinedAt,
            string sessionExpiresAt,
            bool isReconnect,
            RealtimeMovementSettings movementSettings,
            RealtimePlayerState initialPlayerState)
        {
            SimulationSessionId = simulationSessionId;
            AccountId = accountId;
            CharacterId = characterId;
            CharacterName = characterName;
            ShardId = shardId;
            WorldId = worldId;
            ControlledEntityId = controlledEntityId;
            SimulationRevision = simulationRevision;
            CollisionRevision = collisionRevision;
            JoinedAt = joinedAt;
            SessionExpiresAt = sessionExpiresAt;
            IsReconnect = isReconnect;
            MovementSettings = movementSettings;
            InitialPlayerState = initialPlayerState;
        }

        public string SimulationSessionId { get; }

        public string AccountId { get; }

        public string CharacterId { get; }

        public string CharacterName { get; }

        public string ShardId { get; }

        public string WorldId { get; }

        public ulong ControlledEntityId { get; }

        public string SimulationRevision { get; }

        public string CollisionRevision { get; }

        public string JoinedAt { get; }

        public string SessionExpiresAt { get; }

        public bool IsReconnect { get; }

        public RealtimeMovementSettings MovementSettings { get; }

        public RealtimePlayerState InitialPlayerState { get; }
    }

    public sealed class RealtimeMovementSettings
    {
        public RealtimeMovementSettings(
            ushort tickRateHz,
            ushort snapshotRateHz,
            float walkSpeed,
            float sprintSpeed,
            float rotationSpeedDegrees,
            float gravity,
            float maximumFallSpeed,
            float jumpVelocity,
            float groundedVerticalVelocity,
            float groundHeight,
            float minimumX,
            float maximumX,
            float minimumZ,
            float maximumZ,
            float characterRadius,
            float characterHeight,
            float stepHeight,
            float maximumSlopeDegrees,
            float groundSnapDistance,
            float maximumSubstepDistance,
            byte maximumPenetrationIterations)
        {
            TickRateHz = tickRateHz;
            SnapshotRateHz = snapshotRateHz;
            WalkSpeed = walkSpeed;
            SprintSpeed = sprintSpeed;
            RotationSpeedDegrees = rotationSpeedDegrees;
            Gravity = gravity;
            MaximumFallSpeed = maximumFallSpeed;
            JumpVelocity = jumpVelocity;
            GroundedVerticalVelocity = groundedVerticalVelocity;
            GroundHeight = groundHeight;
            MinimumX = minimumX;
            MaximumX = maximumX;
            MinimumZ = minimumZ;
            MaximumZ = maximumZ;
            CharacterRadius = characterRadius;
            CharacterHeight = characterHeight;
            StepHeight = stepHeight;
            MaximumSlopeDegrees = maximumSlopeDegrees;
            GroundSnapDistance = groundSnapDistance;
            MaximumSubstepDistance = maximumSubstepDistance;
            MaximumPenetrationIterations = maximumPenetrationIterations;
        }

        public ushort TickRateHz { get; }

        public ushort SnapshotRateHz { get; }

        public float WalkSpeed { get; }

        public float SprintSpeed { get; }

        public float RotationSpeedDegrees { get; }

        public float Gravity { get; }

        public float MaximumFallSpeed { get; }

        public float JumpVelocity { get; }

        public float GroundedVerticalVelocity { get; }

        public float GroundHeight { get; }

        public float MinimumX { get; }

        public float MaximumX { get; }

        public float MinimumZ { get; }

        public float MaximumZ { get; }

        public float CharacterRadius { get; }

        public float CharacterHeight { get; }

        public float StepHeight { get; }

        public float MaximumSlopeDegrees { get; }

        public float GroundSnapDistance { get; }

        public float MaximumSubstepDistance { get; }

        public byte MaximumPenetrationIterations { get; }
    }

    public sealed class RealtimeMovementInput
    {
        public RealtimeMovementInput(
            uint inputSequence,
            uint clientTick,
            float moveX,
            float moveY,
            float cameraYawDegrees,
            RealtimeMovementButtons buttons)
        {
            InputSequence = inputSequence;
            ClientTick = clientTick;
            MoveX = moveX;
            MoveY = moveY;
            CameraYawDegrees = cameraYawDegrees;
            Buttons = buttons;
        }

        public uint InputSequence { get; }

        public uint ClientTick { get; }

        public float MoveX { get; }

        public float MoveY { get; }

        public float CameraYawDegrees { get; }

        public RealtimeMovementButtons Buttons { get; }
    }

    public sealed class RealtimePlayerState
    {
        public RealtimePlayerState(
            float positionX,
            float positionY,
            float positionZ,
            float velocityX,
            float velocityY,
            float velocityZ,
            float yawDegrees,
            bool isGrounded,
            bool isSprinting)
        {
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            VelocityX = velocityX;
            VelocityY = velocityY;
            VelocityZ = velocityZ;
            YawDegrees = yawDegrees;
            IsGrounded = isGrounded;
            IsSprinting = isSprinting;
        }

        public float PositionX { get; }

        public float PositionY { get; }

        public float PositionZ { get; }

        public float VelocityX { get; }

        public float VelocityY { get; }

        public float VelocityZ { get; }

        public float YawDegrees { get; }

        public bool IsGrounded { get; }

        public bool IsSprinting { get; }
    }

    public sealed class RealtimeEntitySpawn
    {
        public RealtimeEntitySpawn(
            ulong entityId,
            RealtimeEntityKind kind,
            string persistentId,
            string displayName,
            string archetypeId,
            uint serverTick,
            RealtimePlayerState initialState)
        {
            EntityId = entityId;
            Kind = kind;
            PersistentId = persistentId;
            DisplayName = displayName;
            ArchetypeId = archetypeId;
            ServerTick = serverTick;
            InitialState = initialState;
        }

        public ulong EntityId { get; }

        public RealtimeEntityKind Kind { get; }

        public string PersistentId { get; }

        public string DisplayName { get; }

        public string ArchetypeId { get; }

        public uint ServerTick { get; }

        public RealtimePlayerState InitialState { get; }
    }

    public sealed class RealtimeEntityDespawn
    {
        public RealtimeEntityDespawn(ulong entityId, string reason)
        {
            EntityId = entityId;
            Reason = reason;
        }

        public ulong EntityId { get; }

        public string Reason { get; }
    }

    public sealed class RealtimeEntitySnapshot
    {
        public RealtimeEntitySnapshot(
            ulong entityId,
            uint lastProcessedInputSequence,
            RealtimePlayerState state)
        {
            EntityId = entityId;
            LastProcessedInputSequence = lastProcessedInputSequence;
            State = state;
        }

        public ulong EntityId { get; }

        public uint LastProcessedInputSequence { get; }

        public RealtimePlayerState State { get; }
    }

    public sealed class RealtimeSimulationSnapshot
    {
        public RealtimeSimulationSnapshot(
            uint snapshotSequence,
            uint serverTick,
            ushort chunkIndex,
            ushort chunkCount,
            RealtimeEntitySnapshot[] entities)
        {
            SnapshotSequence = snapshotSequence;
            ServerTick = serverTick;
            ChunkIndex = chunkIndex;
            ChunkCount = chunkCount;
            Entities = entities;
        }

        public uint SnapshotSequence { get; }

        public uint ServerTick { get; }

        public ushort ChunkIndex { get; }

        public ushort ChunkCount { get; }

        public RealtimeEntitySnapshot[] Entities { get; }
    }

    public sealed class RealtimeError
    {
        public RealtimeError(string code, string message)
        {
            Code = code;
            Message = message;
        }

        public string Code { get; }

        public string Message { get; }
    }

    public static class RealtimeProtocol
    {
        private const uint Magic = 0x4F4D4D53;
        private const int MaximumJoinTicketLength = 1024;
        private const int MaximumIdentifierLength = 128;
        private const int MaximumNameLength = 128;
        private const int MaximumTimestampLength = 64;
        private const int MaximumErrorCodeLength = 64;
        private const int MaximumErrorMessageLength = 512;

        public const int MaximumInputBatchSize = 4;
        public const int MaximumSnapshotEntitiesPerChunk = 20;
        public const byte ControlChannel = 0;
        public const byte MovementInputChannel = 1;
        public const byte UnreliableReceiveChannel = 0;
        public const byte ChannelCount = 2;

        public const ushort Version = 6;
        public const string ConnectionKey = "ShooterMmo.Realtime.v6";
        public const int MaximumPacketSize = 1200;

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

        public static byte[] EncodeEntitySpawn(RealtimeEntitySpawn spawn)
        {
            if (!IsValidEntitySpawn(spawn))
            {
                throw new ArgumentException("Entity spawn is invalid.", nameof(spawn));
            }

            return Encode(RealtimeMessageType.EntitySpawn, writer =>
            {
                writer.Write(spawn.EntityId);
                writer.Write((byte)spawn.Kind);
                WriteString(writer, spawn.PersistentId, MaximumIdentifierLength, nameof(spawn.PersistentId));
                WriteString(writer, spawn.DisplayName, MaximumNameLength, nameof(spawn.DisplayName));
                WriteString(writer, spawn.ArchetypeId, MaximumIdentifierLength, nameof(spawn.ArchetypeId));
                writer.Write(spawn.ServerTick);
                WritePlayerState(writer, spawn.InitialState);
            });
        }

        public static bool TryDecodeEntitySpawn(
            byte[] data,
            out RealtimeEntitySpawn spawn,
            out string error)
        {
            spawn = null;
            if (!TryCreateReader(data, RealtimeMessageType.EntitySpawn, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadUInt64(reader, out var entityId, out error)
                    || !TryReadByte(reader, out var rawKind, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var persistentId, out error)
                    || !TryReadString(reader, MaximumNameLength, out var displayName, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var archetypeId, out error)
                    || !TryReadUInt32(reader, out var serverTick, out error)
                    || !TryReadPlayerState(reader, out var initialState, out error)
                    || !TryFinish(stream, out error))
                {
                    return false;
                }

                var decoded = new RealtimeEntitySpawn(
                    entityId,
                    (RealtimeEntityKind)rawKind,
                    persistentId,
                    displayName,
                    archetypeId,
                    serverTick,
                    initialState);
                if (!IsValidEntitySpawn(decoded))
                {
                    error = "Entity spawn is invalid.";
                    return false;
                }

                spawn = decoded;
                return true;
            }
        }

        public static byte[] EncodeEntityDespawn(RealtimeEntityDespawn despawn)
        {
            if (despawn == null || despawn.EntityId == 0 || string.IsNullOrWhiteSpace(despawn.Reason))
            {
                throw new ArgumentException("Entity despawn is invalid.", nameof(despawn));
            }

            return Encode(RealtimeMessageType.EntityDespawn, writer =>
            {
                writer.Write(despawn.EntityId);
                WriteString(writer, despawn.Reason, MaximumErrorCodeLength, nameof(despawn.Reason));
            });
        }

        public static bool TryDecodeEntityDespawn(
            byte[] data,
            out RealtimeEntityDespawn despawn,
            out string error)
        {
            despawn = null;
            if (!TryCreateReader(data, RealtimeMessageType.EntityDespawn, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadUInt64(reader, out var entityId, out error)
                    || entityId == 0
                    || !TryReadString(reader, MaximumErrorCodeLength, out var reason, out error)
                    || string.IsNullOrWhiteSpace(reason)
                    || !TryFinish(stream, out error))
                {
                    error = string.IsNullOrEmpty(error) ? "Entity despawn is invalid." : error;
                    return false;
                }

                despawn = new RealtimeEntityDespawn(entityId, reason);
                return true;
            }
        }

        public static byte[] EncodeMovementInputBatch(RealtimeMovementInput[] inputs)
        {
            if (inputs == null || inputs.Length == 0 || inputs.Length > MaximumInputBatchSize)
            {
                throw new ArgumentException(
                    $"Movement input batches require between 1 and {MaximumInputBatchSize} commands.",
                    nameof(inputs));
            }

            return Encode(RealtimeMessageType.MovementInputBatch, writer =>
            {
                writer.Write((byte)inputs.Length);
                for (var index = 0; index < inputs.Length; index++)
                {
                    WriteMovementInput(writer, inputs[index]);
                }
            });
        }

        public static bool TryDecodeMovementInputBatch(
            byte[] data,
            out RealtimeMovementInput[] inputs,
            out string error)
        {
            inputs = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.MovementInputBatch,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadByte(reader, out var count, out error)
                    || count == 0
                    || count > MaximumInputBatchSize)
                {
                    error = "Movement input batch size is invalid.";
                    return false;
                }

                var decoded = new RealtimeMovementInput[count];
                for (var index = 0; index < decoded.Length; index++)
                {
                    if (!TryReadMovementInput(reader, out decoded[index], out error))
                    {
                        return false;
                    }
                }

                if (!TryFinish(stream, out error))
                {
                    return false;
                }

                inputs = decoded;
                return true;
            }
        }

        public static byte[] EncodeSimulationSnapshot(RealtimeSimulationSnapshot snapshot)
        {
            if (snapshot == null
                || snapshot.ChunkCount == 0
                || snapshot.ChunkIndex >= snapshot.ChunkCount
                || snapshot.Entities == null
                || snapshot.Entities.Length == 0
                || snapshot.Entities.Length > MaximumSnapshotEntitiesPerChunk)
            {
                throw new ArgumentException("Simulation snapshot metadata is invalid.", nameof(snapshot));
            }

            return Encode(RealtimeMessageType.SimulationSnapshot, writer =>
            {
                writer.Write(snapshot.SnapshotSequence);
                writer.Write(snapshot.ServerTick);
                writer.Write(snapshot.ChunkIndex);
                writer.Write(snapshot.ChunkCount);
                writer.Write((byte)snapshot.Entities.Length);
                for (var index = 0; index < snapshot.Entities.Length; index++)
                {
                    WriteEntitySnapshot(writer, snapshot.Entities[index]);
                }
            });
        }

        public static bool TryDecodeSimulationSnapshot(
            byte[] data,
            out RealtimeSimulationSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.SimulationSnapshot,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadUInt32(reader, out var snapshotSequence, out error)
                    || !TryReadUInt32(reader, out var serverTick, out error)
                    || !TryReadUInt16(reader, out var chunkIndex, out error)
                    || !TryReadUInt16(reader, out var chunkCount, out error)
                    || !TryReadByte(reader, out var entityCount, out error))
                {
                    return false;
                }

                if (chunkCount == 0
                    || chunkIndex >= chunkCount
                    || entityCount == 0
                    || entityCount > MaximumSnapshotEntitiesPerChunk)
                {
                    error = "Simulation snapshot metadata is invalid.";
                    return false;
                }

                var entities = new RealtimeEntitySnapshot[entityCount];
                for (var index = 0; index < entities.Length; index++)
                {
                    if (!TryReadEntitySnapshot(reader, out entities[index], out error))
                    {
                        return false;
                    }
                }

                if (!TryFinish(stream, out error))
                {
                    return false;
                }

                snapshot = new RealtimeSimulationSnapshot(
                    snapshotSequence,
                    serverTick,
                    chunkIndex,
                    chunkCount,
                    entities);
                return true;
            }
        }

        public static bool TryReadMessageType(byte[] data, out RealtimeMessageType messageType)
        {
            messageType = default;
            if (data == null || data.Length < sizeof(uint) + sizeof(ushort) + sizeof(byte))
            {
                return false;
            }

            using (var stream = new MemoryStream(data, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                return reader.ReadUInt32() == Magic
                    && reader.ReadUInt16() == Version
                    && Enum.IsDefined(typeof(RealtimeMessageType), messageType = (RealtimeMessageType)reader.ReadByte());
            }
        }

        private static byte[] Encode(RealtimeMessageType messageType, Action<BinaryWriter> writePayload)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write((byte)messageType);
                writePayload(writer);
                writer.Flush();

                if (stream.Length > MaximumPacketSize)
                {
                    throw new InvalidOperationException(
                        $"Realtime packet exceeds the {MaximumPacketSize} byte limit.");
                }

                return stream.ToArray();
            }
        }

        private static byte[] EncodeError(RealtimeMessageType messageType, string code, string message)
        {
            return Encode(messageType, writer =>
            {
                WriteString(writer, code, MaximumErrorCodeLength, nameof(code));
                WriteString(writer, message, MaximumErrorMessageLength, nameof(message));
            });
        }

        private static bool TryDecodeError(
            byte[] data,
            RealtimeMessageType expectedType,
            out RealtimeError realtimeError,
            out string error)
        {
            realtimeError = null;
            if (!TryCreateReader(data, expectedType, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadString(reader, MaximumErrorCodeLength, out var code, out error)
                    || !TryReadString(reader, MaximumErrorMessageLength, out var message, out error)
                    || !TryFinish(stream, out error))
                {
                    return false;
                }

                realtimeError = new RealtimeError(code, message);
                return true;
            }
        }

        private static bool TryCreateReader(
            byte[] data,
            RealtimeMessageType expectedType,
            out MemoryStream stream,
            out BinaryReader reader,
            out string error)
        {
            stream = null;
            reader = null;
            error = string.Empty;

            if (data == null || data.Length > MaximumPacketSize)
            {
                error = "Packet length is invalid.";
                return false;
            }

            try
            {
                stream = new MemoryStream(data, writable: false);
                reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                if (reader.ReadUInt32() != Magic)
                {
                    error = "Packet magic is invalid.";
                    DisposeReader(stream, reader);
                    return false;
                }

                if (reader.ReadUInt16() != Version)
                {
                    error = "Protocol version is unsupported.";
                    DisposeReader(stream, reader);
                    return false;
                }

                if ((RealtimeMessageType)reader.ReadByte() != expectedType)
                {
                    error = "Packet message type is invalid.";
                    DisposeReader(stream, reader);
                    return false;
                }

                return true;
            }
            catch (EndOfStreamException)
            {
                error = "Packet header is incomplete.";
                DisposeReader(stream, reader);
                return false;
            }
        }

        private static bool TryReadString(
            BinaryReader reader,
            int maximumLength,
            out string value,
            out string error)
        {
            value = string.Empty;
            error = string.Empty;

            try
            {
                var byteLength = reader.ReadUInt16();
                if (byteLength == 0 || byteLength > maximumLength * 4)
                {
                    error = "Packet string length is invalid.";
                    return false;
                }

                var bytes = reader.ReadBytes(byteLength);
                if (bytes.Length != byteLength)
                {
                    error = "Packet string is incomplete.";
                    return false;
                }

                value = new UTF8Encoding(false, true).GetString(bytes);
                if (value.Length > maximumLength)
                {
                    error = "Packet string exceeds its character limit.";
                    return false;
                }

                return true;
            }
            catch (Exception exception) when (
                exception is EndOfStreamException || exception is DecoderFallbackException)
            {
                error = "Packet string is invalid.";
                return false;
            }
        }

        private static bool TryReadBoolean(BinaryReader reader, out bool value, out string error)
        {
            value = false;
            error = string.Empty;

            try
            {
                value = reader.ReadBoolean();
                return true;
            }
            catch (EndOfStreamException)
            {
                error = "Packet boolean is incomplete.";
                return false;
            }
        }

        private static void WriteMovementInput(BinaryWriter writer, RealtimeMovementInput input)
        {
            if (input == null
                || !IsUnitInput(input.MoveX, input.MoveY)
                || !IsFinite(input.CameraYawDegrees)
                || !AreValidMovementButtons(input.Buttons))
            {
                throw new ArgumentException("Movement input is invalid.", nameof(input));
            }

            writer.Write(input.InputSequence);
            writer.Write(input.ClientTick);
            writer.Write(input.MoveX);
            writer.Write(input.MoveY);
            writer.Write(input.CameraYawDegrees);
            writer.Write((byte)input.Buttons);
        }

        private static bool TryReadMovementInput(
            BinaryReader reader,
            out RealtimeMovementInput input,
            out string error)
        {
            input = null;
            if (!TryReadUInt32(reader, out var inputSequence, out error)
                || !TryReadUInt32(reader, out var clientTick, out error)
                || !TryReadSingle(reader, out var moveX, out error)
                || !TryReadSingle(reader, out var moveY, out error)
                || !TryReadSingle(reader, out var cameraYaw, out error)
                || !TryReadByte(reader, out var rawButtons, out error))
            {
                return false;
            }

            var buttons = (RealtimeMovementButtons)rawButtons;
            if (!IsUnitInput(moveX, moveY) || !AreValidMovementButtons(buttons))
            {
                error = "Movement input values are invalid.";
                return false;
            }

            input = new RealtimeMovementInput(
                inputSequence,
                clientTick,
                moveX,
                moveY,
                cameraYaw,
                buttons);
            return true;
        }

        private static void WriteMovementSettings(BinaryWriter writer, RealtimeMovementSettings settings)
        {
            if (!AreValidMovementSettings(settings))
            {
                throw new ArgumentException("Movement settings are invalid.", nameof(settings));
            }

            writer.Write(settings.TickRateHz);
            writer.Write(settings.SnapshotRateHz);
            writer.Write(settings.WalkSpeed);
            writer.Write(settings.SprintSpeed);
            writer.Write(settings.RotationSpeedDegrees);
            writer.Write(settings.Gravity);
            writer.Write(settings.MaximumFallSpeed);
            writer.Write(settings.JumpVelocity);
            writer.Write(settings.GroundedVerticalVelocity);
            writer.Write(settings.GroundHeight);
            writer.Write(settings.MinimumX);
            writer.Write(settings.MaximumX);
            writer.Write(settings.MinimumZ);
            writer.Write(settings.MaximumZ);
            writer.Write(settings.CharacterRadius);
            writer.Write(settings.CharacterHeight);
            writer.Write(settings.StepHeight);
            writer.Write(settings.MaximumSlopeDegrees);
            writer.Write(settings.GroundSnapDistance);
            writer.Write(settings.MaximumSubstepDistance);
            writer.Write(settings.MaximumPenetrationIterations);
        }

        private static bool TryReadMovementSettings(
            BinaryReader reader,
            out RealtimeMovementSettings settings,
            out string error)
        {
            settings = null;
            if (!TryReadUInt16(reader, out var tickRateHz, out error)
                || !TryReadUInt16(reader, out var snapshotRateHz, out error)
                || !TryReadSingle(reader, out var walkSpeed, out error)
                || !TryReadSingle(reader, out var sprintSpeed, out error)
                || !TryReadSingle(reader, out var rotationSpeed, out error)
                || !TryReadSingle(reader, out var gravity, out error)
                || !TryReadSingle(reader, out var maximumFallSpeed, out error)
                || !TryReadSingle(reader, out var jumpVelocity, out error)
                || !TryReadSingle(reader, out var groundedVerticalVelocity, out error)
                || !TryReadSingle(reader, out var groundHeight, out error)
                || !TryReadSingle(reader, out var minimumX, out error)
                || !TryReadSingle(reader, out var maximumX, out error)
                || !TryReadSingle(reader, out var minimumZ, out error)
                || !TryReadSingle(reader, out var maximumZ, out error)
                || !TryReadSingle(reader, out var characterRadius, out error)
                || !TryReadSingle(reader, out var characterHeight, out error)
                || !TryReadSingle(reader, out var stepHeight, out error)
                || !TryReadSingle(reader, out var maximumSlopeDegrees, out error)
                || !TryReadSingle(reader, out var groundSnapDistance, out error)
                || !TryReadSingle(reader, out var maximumSubstepDistance, out error)
                || !TryReadByte(reader, out var maximumPenetrationIterations, out error))
            {
                return false;
            }

            var decoded = new RealtimeMovementSettings(
                tickRateHz,
                snapshotRateHz,
                walkSpeed,
                sprintSpeed,
                rotationSpeed,
                gravity,
                maximumFallSpeed,
                jumpVelocity,
                groundedVerticalVelocity,
                groundHeight,
                minimumX,
                maximumX,
                minimumZ,
                maximumZ,
                characterRadius,
                characterHeight,
                stepHeight,
                maximumSlopeDegrees,
                groundSnapDistance,
                maximumSubstepDistance,
                maximumPenetrationIterations);
            if (!AreValidMovementSettings(decoded))
            {
                error = "Movement settings are invalid.";
                return false;
            }

            settings = decoded;
            return true;
        }

        private static void WriteEntitySnapshot(BinaryWriter writer, RealtimeEntitySnapshot snapshot)
        {
            if (snapshot == null || snapshot.EntityId == 0)
            {
                throw new ArgumentException("Entity snapshot id is invalid.", nameof(snapshot));
            }

            writer.Write(snapshot.EntityId);
            writer.Write(snapshot.LastProcessedInputSequence);
            WritePlayerState(writer, snapshot.State);
        }

        private static bool TryReadEntitySnapshot(
            BinaryReader reader,
            out RealtimeEntitySnapshot snapshot,
            out string error)
        {
            snapshot = null;
            if (!TryReadUInt64(reader, out var entityId, out error)
                || !TryReadUInt32(reader, out var lastProcessedInputSequence, out error)
                || !TryReadPlayerState(reader, out var state, out error))
            {
                return false;
            }

            if (entityId == 0)
            {
                error = "Entity snapshot id is invalid.";
                return false;
            }

            snapshot = new RealtimeEntitySnapshot(entityId, lastProcessedInputSequence, state);
            return true;
        }

        private static void WritePlayerState(BinaryWriter writer, RealtimePlayerState state)
        {
            if (!IsValidPlayerState(state))
            {
                throw new ArgumentException("Player movement state is invalid.", nameof(state));
            }

            writer.Write(state.PositionX);
            writer.Write(state.PositionY);
            writer.Write(state.PositionZ);
            writer.Write(state.VelocityX);
            writer.Write(state.VelocityY);
            writer.Write(state.VelocityZ);
            writer.Write(state.YawDegrees);
            byte flags = 0;
            if (state.IsGrounded)
            {
                flags |= 1;
            }

            if (state.IsSprinting)
            {
                flags |= 2;
            }

            writer.Write(flags);
        }

        private static bool TryReadPlayerState(
            BinaryReader reader,
            out RealtimePlayerState state,
            out string error)
        {
            state = null;
            if (!TryReadSingle(reader, out var positionX, out error)
                || !TryReadSingle(reader, out var positionY, out error)
                || !TryReadSingle(reader, out var positionZ, out error)
                || !TryReadSingle(reader, out var velocityX, out error)
                || !TryReadSingle(reader, out var velocityY, out error)
                || !TryReadSingle(reader, out var velocityZ, out error)
                || !TryReadSingle(reader, out var yawDegrees, out error)
                || !TryReadByte(reader, out var flags, out error))
            {
                return false;
            }

            if ((flags & ~3) != 0)
            {
                error = "Player movement state flags are invalid.";
                return false;
            }

            var decoded = new RealtimePlayerState(
                positionX,
                positionY,
                positionZ,
                velocityX,
                velocityY,
                velocityZ,
                yawDegrees,
                (flags & 1) != 0,
                (flags & 2) != 0);
            if (!IsValidPlayerState(decoded))
            {
                error = "Player movement state is invalid.";
                return false;
            }

            state = decoded;
            return true;
        }

        private static bool TryReadByte(BinaryReader reader, out byte value, out string error)
        {
            try
            {
                value = reader.ReadByte();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet byte is incomplete.";
                return false;
            }
        }

        private static bool TryReadUInt16(BinaryReader reader, out ushort value, out string error)
        {
            try
            {
                value = reader.ReadUInt16();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet unsigned integer is incomplete.";
                return false;
            }
        }

        private static bool TryReadUInt32(BinaryReader reader, out uint value, out string error)
        {
            try
            {
                value = reader.ReadUInt32();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet unsigned integer is incomplete.";
                return false;
            }
        }

        private static bool TryReadUInt64(BinaryReader reader, out ulong value, out string error)
        {
            try
            {
                value = reader.ReadUInt64();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet unsigned integer is incomplete.";
                return false;
            }
        }

        private static bool IsValidEntitySpawn(RealtimeEntitySpawn spawn)
        {
            return spawn != null
                && spawn.EntityId != 0
                && Enum.IsDefined(typeof(RealtimeEntityKind), spawn.Kind)
                && Guid.TryParse(spawn.PersistentId, out _)
                && !string.IsNullOrWhiteSpace(spawn.DisplayName)
                && !string.IsNullOrWhiteSpace(spawn.ArchetypeId)
                && IsValidPlayerState(spawn.InitialState);
        }

        private static bool TryReadSingle(BinaryReader reader, out float value, out string error)
        {
            try
            {
                value = reader.ReadSingle();
                if (!IsFinite(value))
                {
                    error = "Packet number is not finite.";
                    return false;
                }

                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0f;
                error = "Packet number is incomplete.";
                return false;
            }
        }

        private static bool AreValidMovementSettings(RealtimeMovementSettings settings)
        {
            return settings != null
                && settings.TickRateHz > 0
                && settings.TickRateHz <= 120
                && settings.SnapshotRateHz > 0
                && settings.SnapshotRateHz <= settings.TickRateHz
                && settings.TickRateHz % settings.SnapshotRateHz == 0
                && IsFinite(settings.WalkSpeed)
                && settings.WalkSpeed > 0f
                && IsFinite(settings.SprintSpeed)
                && settings.SprintSpeed >= settings.WalkSpeed
                && IsFinite(settings.RotationSpeedDegrees)
                && settings.RotationSpeedDegrees > 0f
                && IsFinite(settings.Gravity)
                && settings.Gravity < 0f
                && IsFinite(settings.MaximumFallSpeed)
                && settings.MaximumFallSpeed > 0f
                && IsFinite(settings.JumpVelocity)
                && settings.JumpVelocity > 0f
                && IsFinite(settings.GroundedVerticalVelocity)
                && settings.GroundedVerticalVelocity <= 0f
                && IsFinite(settings.GroundHeight)
                && IsFinite(settings.MinimumX)
                && IsFinite(settings.MaximumX)
                && IsFinite(settings.MinimumZ)
                && IsFinite(settings.MaximumZ)
                && settings.MinimumX < settings.MaximumX
                && settings.MinimumZ < settings.MaximumZ
                && IsFinite(settings.CharacterRadius)
                && settings.CharacterRadius > 0f
                && IsFinite(settings.CharacterHeight)
                && settings.CharacterHeight >= settings.CharacterRadius * 2f
                && IsFinite(settings.StepHeight)
                && settings.StepHeight >= 0f
                && settings.StepHeight < settings.CharacterHeight
                && IsFinite(settings.MaximumSlopeDegrees)
                && settings.MaximumSlopeDegrees > 0f
                && settings.MaximumSlopeDegrees < 89f
                && IsFinite(settings.GroundSnapDistance)
                && settings.GroundSnapDistance >= 0f
                && IsFinite(settings.MaximumSubstepDistance)
                && settings.MaximumSubstepDistance > 0f
                && settings.MaximumSubstepDistance <= settings.CharacterRadius
                && settings.MaximumPenetrationIterations > 0
                && settings.MaximumPenetrationIterations <= 16;
        }

        private static bool IsValidPlayerState(RealtimePlayerState state)
        {
            return state != null
                && IsFinite(state.PositionX)
                && IsFinite(state.PositionY)
                && IsFinite(state.PositionZ)
                && IsFinite(state.VelocityX)
                && IsFinite(state.VelocityY)
                && IsFinite(state.VelocityZ)
                && IsFinite(state.YawDegrees);
        }

        private static bool IsUnitInput(float moveX, float moveY)
        {
            if (!IsFinite(moveX) || !IsFinite(moveY))
            {
                return false;
            }

            return (moveX * moveX) + (moveY * moveY) <= 1.0001f;
        }

        private static bool AreValidMovementButtons(RealtimeMovementButtons buttons)
        {
            const RealtimeMovementButtons all = RealtimeMovementButtons.Sprint
                | RealtimeMovementButtons.Jump
                | RealtimeMovementButtons.Aim;
            return (buttons & ~all) == 0;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool TryFinish(MemoryStream stream, out string error)
        {
            if (stream.Position == stream.Length)
            {
                error = string.Empty;
                return true;
            }

            error = "Packet contains trailing data.";
            return false;
        }

        private static void WriteString(
            BinaryWriter writer,
            string value,
            int maximumLength,
            string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            {
                throw new ArgumentException(
                    $"Value must contain between 1 and {maximumLength} characters.",
                    parameterName);
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException("Value is too large to encode.", parameterName);
            }

            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static void DisposeReader(MemoryStream stream, BinaryReader reader)
        {
            reader?.Dispose();
            stream?.Dispose();
        }
    }
}
