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
        EntityDespawn = 11,
        CarryStateChanged = 12,
        ItemOperationIntent = 13,
        ItemOperationResult = 14,
        CorpsePresenceSnapshotChunk = 15,
        CorpseInteractionIntent = 16,
        CorpseInteractionResult = 17,
        CorpseViewStateChunk = 18,
        CorpseViewClosed = 19
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

    public enum RealtimeItemOperationKind : byte
    {
        Relocate = 1,
        Equip = 2,
        Unequip = 3,
        SplitStack = 4,
        MergeStacks = 5,
        Destroy = 6,
        ClaimRecoveryDelivery = 7,
        SwapContainerItems = 8
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
            RealtimeCarryState carryState,
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
            CarryState = carryState;
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

        public RealtimeCarryState CarryState { get; }

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

    public sealed class RealtimeCarryState
    {
        public RealtimeCarryState(
            long itemStateRevision,
            long carriedWeight,
            long carryCapacity)
        {
            ItemStateRevision = itemStateRevision;
            CarriedWeight = carriedWeight;
            CarryCapacity = carryCapacity;
        }

        public long ItemStateRevision { get; }

        public long CarriedWeight { get; }

        public long CarryCapacity { get; }
    }

    public sealed class RealtimeItemRevisionExpectation
    {
        public RealtimeItemRevisionExpectation(Guid itemInstanceId, long revision)
        {
            ItemInstanceId = itemInstanceId;
            Revision = revision;
        }

        public Guid ItemInstanceId { get; }

        public long Revision { get; }
    }

    public sealed class RealtimeItemOperationIntent
    {
        internal RealtimeItemOperationIntent(
            Guid operationId,
            RealtimeItemOperationKind operationKind,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision,
            Guid destinationContainerId,
            int destinationSlotIndex,
            int quantity,
            string equipmentSlotId,
            Guid recoveryDeliveryId,
            long expectedRecoveryDeliveryRevision,
            RealtimeItemRevisionExpectation[] items)
        {
            OperationId = operationId;
            OperationKind = operationKind;
            ExpectedCharacterRevision = expectedCharacterRevision;
            ItemInstanceId = itemInstanceId;
            ExpectedItemRevision = expectedItemRevision;
            TargetItemInstanceId = targetItemInstanceId;
            ExpectedTargetItemRevision = expectedTargetItemRevision;
            DestinationContainerId = destinationContainerId;
            DestinationSlotIndex = destinationSlotIndex;
            Quantity = quantity;
            EquipmentSlotId = equipmentSlotId;
            RecoveryDeliveryId = recoveryDeliveryId;
            ExpectedRecoveryDeliveryRevision = expectedRecoveryDeliveryRevision;
            Items = items;
        }

        public Guid OperationId { get; }

        public RealtimeItemOperationKind OperationKind { get; }

        public long ExpectedCharacterRevision { get; }

        public Guid ItemInstanceId { get; }

        public long ExpectedItemRevision { get; }

        public Guid TargetItemInstanceId { get; }

        public long ExpectedTargetItemRevision { get; }

        public Guid DestinationContainerId { get; }

        public int DestinationSlotIndex { get; }

        public int Quantity { get; }

        public string EquipmentSlotId { get; }

        public Guid RecoveryDeliveryId { get; }

        public long ExpectedRecoveryDeliveryRevision { get; }

        public RealtimeItemRevisionExpectation[] Items { get; }

        public static RealtimeItemOperationIntent CreateRelocate(
            Guid operationId,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            int destinationSlotIndex = -1)
        {
            return CreateItemDestinationOperation(
                operationId,
                RealtimeItemOperationKind.Relocate,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                destinationContainerId,
                destinationSlotIndex);
        }

        public static RealtimeItemOperationIntent CreateEquip(
            Guid operationId,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            string equipmentSlotId)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.Equip,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                Guid.Empty,
                0,
                Guid.Empty,
                -1,
                0,
                equipmentSlotId,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }

        public static RealtimeItemOperationIntent CreateUnequip(
            Guid operationId,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            int destinationSlotIndex = -1)
        {
            return CreateItemDestinationOperation(
                operationId,
                RealtimeItemOperationKind.Unequip,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                destinationContainerId,
                destinationSlotIndex);
        }

        public static RealtimeItemOperationIntent CreateSplitStack(
            Guid operationId,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            int quantity,
            Guid destinationContainerId,
            int destinationSlotIndex = -1)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.SplitStack,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                Guid.Empty,
                0,
                destinationContainerId,
                destinationSlotIndex,
                quantity,
                string.Empty,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }

        public static RealtimeItemOperationIntent CreateMergeStacks(
            Guid operationId,
            long expectedCharacterRevision,
            Guid sourceItemInstanceId,
            long expectedSourceItemRevision,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.MergeStacks,
                expectedCharacterRevision,
                sourceItemInstanceId,
                expectedSourceItemRevision,
                targetItemInstanceId,
                expectedTargetItemRevision,
                Guid.Empty,
                -1,
                0,
                string.Empty,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }

        public static RealtimeItemOperationIntent CreateSwapContainerItems(
            Guid operationId,
            long expectedCharacterRevision,
            Guid firstItemInstanceId,
            long expectedFirstItemRevision,
            Guid secondItemInstanceId,
            long expectedSecondItemRevision)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.SwapContainerItems,
                expectedCharacterRevision,
                firstItemInstanceId,
                expectedFirstItemRevision,
                secondItemInstanceId,
                expectedSecondItemRevision,
                Guid.Empty,
                -1,
                0,
                string.Empty,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }

        public static RealtimeItemOperationIntent CreateDestroy(
            Guid operationId,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.Destroy,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                Guid.Empty,
                0,
                Guid.Empty,
                -1,
                0,
                string.Empty,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }

        public static RealtimeItemOperationIntent CreateClaimRecoveryDelivery(
            Guid operationId,
            long expectedCharacterRevision,
            Guid recoveryDeliveryId,
            long expectedRecoveryDeliveryRevision,
            Guid destinationContainerId,
            RealtimeItemRevisionExpectation[] items)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.ClaimRecoveryDelivery,
                expectedCharacterRevision,
                Guid.Empty,
                0,
                Guid.Empty,
                0,
                destinationContainerId,
                -1,
                0,
                string.Empty,
                recoveryDeliveryId,
                expectedRecoveryDeliveryRevision,
                items);
        }

        private static RealtimeItemOperationIntent CreateItemDestinationOperation(
            Guid operationId,
            RealtimeItemOperationKind operationKind,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            int destinationSlotIndex)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                operationKind,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                Guid.Empty,
                0,
                destinationContainerId,
                destinationSlotIndex,
                0,
                string.Empty,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }
    }

    public sealed class RealtimeItemRevision
    {
        public RealtimeItemRevision(Guid itemInstanceId, long revision)
        {
            ItemInstanceId = itemInstanceId;
            Revision = revision;
        }

        public Guid ItemInstanceId { get; }

        public long Revision { get; }
    }

    public sealed class RealtimeContainerRevision
    {
        public RealtimeContainerRevision(Guid containerId, long revision)
        {
            ContainerId = containerId;
            Revision = revision;
        }

        public Guid ContainerId { get; }

        public long Revision { get; }
    }

    public sealed class RealtimeItemOperationResult
    {
        public RealtimeItemOperationResult(
            Guid operationId,
            RealtimeItemOperationKind operationKind,
            bool succeeded,
            bool requiresInventoryRefresh,
            RealtimeError error,
            RealtimeCarryState carryState,
            RealtimeItemRevision[] itemRevisions,
            RealtimeContainerRevision[] containerRevisions,
            Guid[] recoveryDeliveryIds)
        {
            OperationId = operationId;
            OperationKind = operationKind;
            Succeeded = succeeded;
            RequiresInventoryRefresh = requiresInventoryRefresh;
            Error = error;
            CarryState = carryState;
            ItemRevisions = itemRevisions;
            ContainerRevisions = containerRevisions;
            RecoveryDeliveryIds = recoveryDeliveryIds;
        }

        public Guid OperationId { get; }

        public RealtimeItemOperationKind OperationKind { get; }

        public bool Succeeded { get; }

        public bool RequiresInventoryRefresh { get; }

        public RealtimeError Error { get; }

        public RealtimeCarryState CarryState { get; }

        public RealtimeItemRevision[] ItemRevisions { get; }

        public RealtimeContainerRevision[] ContainerRevisions { get; }

        public Guid[] RecoveryDeliveryIds { get; }
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

    public static partial class RealtimeProtocol
    {
        private const uint Magic = 0x4F4D4D53;
        private const int MaximumJoinTicketLength = 1024;
        private const int MaximumIdentifierLength = 128;
        private const int MaximumNameLength = 128;
        private const int MaximumTimestampLength = 64;
        private const int MaximumErrorCodeLength = 64;
        private const int MaximumErrorMessageLength = 512;
        private const int MaximumEquipmentSlotIdLength = 64;
        private const int MaximumItemOperationItems = 24;
        private const int MaximumItemResultItemRevisions = 32;
        private const int MaximumItemResultContainerRevisions = 8;
        private const int MaximumItemResultRecoveryDeliveries = 8;

        public const int MaximumInputBatchSize = 4;
        public const int MaximumSnapshotEntitiesPerChunk = 20;
        public const byte ControlChannel = 0;
        public const byte MovementInputChannel = 1;
        public const byte UnreliableReceiveChannel = 0;
        public const byte ChannelCount = 2;

        public const ushort Version = 9;
        public const string ConnectionKey = "ShooterMmo.Realtime.v8";
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

        public static byte[] EncodeCarryStateChanged(RealtimeCarryState carryState)
        {
            return Encode(RealtimeMessageType.CarryStateChanged, writer =>
            {
                WriteCarryState(writer, carryState);
            });
        }

        public static bool TryDecodeCarryStateChanged(
            byte[] data,
            out RealtimeCarryState carryState,
            out string error)
        {
            carryState = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.CarryStateChanged,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                return TryReadCarryState(reader, out carryState, out error)
                    && TryFinish(stream, out error);
            }
        }

        public static byte[] EncodeItemOperationIntent(RealtimeItemOperationIntent intent)
        {
            if (!IsValidItemOperationIntent(intent))
            {
                throw new ArgumentException("Item operation intent is invalid.", nameof(intent));
            }

            return Encode(RealtimeMessageType.ItemOperationIntent, writer =>
            {
                WriteGuid(writer, intent.OperationId);
                writer.Write((byte)intent.OperationKind);
                writer.Write(intent.ExpectedCharacterRevision);
                switch (intent.OperationKind)
                {
                    case RealtimeItemOperationKind.Relocate:
                    case RealtimeItemOperationKind.Unequip:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        WriteGuid(writer, intent.DestinationContainerId);
                        writer.Write(intent.DestinationSlotIndex);
                        break;
                    case RealtimeItemOperationKind.Equip:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        WriteString(
                            writer,
                            intent.EquipmentSlotId,
                            MaximumEquipmentSlotIdLength,
                            nameof(intent.EquipmentSlotId));
                        break;
                    case RealtimeItemOperationKind.SplitStack:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        writer.Write(intent.Quantity);
                        WriteGuid(writer, intent.DestinationContainerId);
                        writer.Write(intent.DestinationSlotIndex);
                        break;
                    case RealtimeItemOperationKind.MergeStacks:
                    case RealtimeItemOperationKind.SwapContainerItems:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        WriteItemExpectation(
                            writer,
                            intent.TargetItemInstanceId,
                            intent.ExpectedTargetItemRevision);
                        break;
                    case RealtimeItemOperationKind.Destroy:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        break;
                    case RealtimeItemOperationKind.ClaimRecoveryDelivery:
                        WriteGuid(writer, intent.RecoveryDeliveryId);
                        writer.Write(intent.ExpectedRecoveryDeliveryRevision);
                        WriteGuid(writer, intent.DestinationContainerId);
                        writer.Write((byte)intent.Items.Length);
                        for (var index = 0; index < intent.Items.Length; index++)
                        {
                            WriteItemExpectation(
                                writer,
                                intent.Items[index].ItemInstanceId,
                                intent.Items[index].Revision);
                        }

                        break;
                    default:
                        throw new ArgumentException(
                            "Item operation kind is invalid.",
                            nameof(intent));
                }
            });
        }

        public static bool TryDecodeItemOperationIntent(
            byte[] data,
            out RealtimeItemOperationIntent intent,
            out string error)
        {
            intent = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.ItemOperationIntent,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadGuid(reader, out var operationId, out error)
                    || !TryReadByte(reader, out var rawOperationKind, out error)
                    || !TryReadInt64(reader, out var expectedCharacterRevision, out error))
                {
                    return false;
                }

                var operationKind = (RealtimeItemOperationKind)rawOperationKind;
                RealtimeItemOperationIntent decoded;
                switch (operationKind)
                {
                    case RealtimeItemOperationKind.Relocate:
                    case RealtimeItemOperationKind.Unequip:
                        if (!TryReadItemExpectation(
                                reader,
                                out var itemId,
                                out var itemRevision,
                                out error)
                            || !TryReadGuid(reader, out var destinationId, out error)
                            || !TryReadInt32(reader, out var destinationSlot, out error))
                        {
                            return false;
                        }

                        decoded = new RealtimeItemOperationIntent(
                            operationId,
                            operationKind,
                            expectedCharacterRevision,
                            itemId,
                            itemRevision,
                            Guid.Empty,
                            0,
                            destinationId,
                            destinationSlot,
                            0,
                            string.Empty,
                            Guid.Empty,
                            0,
                            Array.Empty<RealtimeItemRevisionExpectation>());
                        break;
                    case RealtimeItemOperationKind.Equip:
                        if (!TryReadItemExpectation(
                                reader,
                                out itemId,
                                out itemRevision,
                                out error)
                            || !TryReadString(
                                reader,
                                MaximumEquipmentSlotIdLength,
                                out var equipmentSlotId,
                                out error))
                        {
                            return false;
                        }

                        decoded = RealtimeItemOperationIntent.CreateEquip(
                            operationId,
                            expectedCharacterRevision,
                            itemId,
                            itemRevision,
                            equipmentSlotId);
                        break;
                    case RealtimeItemOperationKind.SplitStack:
                        if (!TryReadItemExpectation(
                                reader,
                                out itemId,
                                out itemRevision,
                                out error)
                            || !TryReadInt32(reader, out var quantity, out error)
                            || !TryReadGuid(reader, out destinationId, out error)
                            || !TryReadInt32(reader, out destinationSlot, out error))
                        {
                            return false;
                        }

                        decoded = RealtimeItemOperationIntent.CreateSplitStack(
                            operationId,
                            expectedCharacterRevision,
                            itemId,
                            itemRevision,
                            quantity,
                            destinationId,
                            destinationSlot);
                        break;
                    case RealtimeItemOperationKind.MergeStacks:
                    case RealtimeItemOperationKind.SwapContainerItems:
                        if (!TryReadItemExpectation(
                                reader,
                                out itemId,
                                out itemRevision,
                                out error)
                            || !TryReadItemExpectation(
                                reader,
                                out var targetItemId,
                                out var targetItemRevision,
                                out error))
                        {
                            return false;
                        }

                        decoded = operationKind == RealtimeItemOperationKind.MergeStacks
                            ? RealtimeItemOperationIntent.CreateMergeStacks(
                                operationId,
                                expectedCharacterRevision,
                                itemId,
                                itemRevision,
                                targetItemId,
                                targetItemRevision)
                            : RealtimeItemOperationIntent.CreateSwapContainerItems(
                                operationId,
                                expectedCharacterRevision,
                                itemId,
                                itemRevision,
                                targetItemId,
                                targetItemRevision);
                        break;
                    case RealtimeItemOperationKind.Destroy:
                        if (!TryReadItemExpectation(
                                reader,
                                out itemId,
                                out itemRevision,
                                out error))
                        {
                            return false;
                        }

                        decoded = RealtimeItemOperationIntent.CreateDestroy(
                            operationId,
                            expectedCharacterRevision,
                            itemId,
                            itemRevision);
                        break;
                    case RealtimeItemOperationKind.ClaimRecoveryDelivery:
                        if (!TryReadGuid(reader, out var recoveryDeliveryId, out error)
                            || !TryReadInt64(
                                reader,
                                out var expectedRecoveryRevision,
                                out error)
                            || !TryReadGuid(reader, out destinationId, out error)
                            || !TryReadByte(reader, out var itemCount, out error)
                            || itemCount == 0
                            || itemCount > MaximumItemOperationItems)
                        {
                            if (string.IsNullOrEmpty(error))
                            {
                                error = "Recovery claim item count is invalid.";
                            }

                            return false;
                        }

                        var items = new RealtimeItemRevisionExpectation[itemCount];
                        for (var index = 0; index < itemCount; index++)
                        {
                            if (!TryReadItemExpectation(
                                    reader,
                                    out var recoveryItemId,
                                    out var recoveryItemRevision,
                                    out error))
                            {
                                return false;
                            }

                            items[index] = new RealtimeItemRevisionExpectation(
                                recoveryItemId,
                                recoveryItemRevision);
                        }

                        decoded = RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
                            operationId,
                            expectedCharacterRevision,
                            recoveryDeliveryId,
                            expectedRecoveryRevision,
                            destinationId,
                            items);
                        break;
                    default:
                        error = "Item operation kind is invalid.";
                        return false;
                }

                if (!IsValidItemOperationIntent(decoded) || !TryFinish(stream, out error))
                {
                    if (string.IsNullOrEmpty(error))
                    {
                        error = "Item operation intent values are invalid.";
                    }

                    return false;
                }

                intent = decoded;
                return true;
            }
        }

        public static byte[] EncodeItemOperationResult(RealtimeItemOperationResult result)
        {
            if (!IsValidItemOperationResult(result))
            {
                throw new ArgumentException("Item operation result is invalid.", nameof(result));
            }

            return Encode(RealtimeMessageType.ItemOperationResult, writer =>
            {
                WriteGuid(writer, result.OperationId);
                writer.Write((byte)result.OperationKind);
                writer.Write(result.Succeeded);
                writer.Write(result.RequiresInventoryRefresh);
                WriteCarryState(writer, result.CarryState);
                if (!result.Succeeded)
                {
                    WriteString(
                        writer,
                        result.Error.Code,
                        MaximumErrorCodeLength,
                        nameof(result.Error.Code));
                    WriteString(
                        writer,
                        result.Error.Message,
                        MaximumErrorMessageLength,
                        nameof(result.Error.Message));
                }

                writer.Write((byte)result.ItemRevisions.Length);
                for (var index = 0; index < result.ItemRevisions.Length; index++)
                {
                    WriteItemExpectation(
                        writer,
                        result.ItemRevisions[index].ItemInstanceId,
                        result.ItemRevisions[index].Revision);
                }

                writer.Write((byte)result.ContainerRevisions.Length);
                for (var index = 0; index < result.ContainerRevisions.Length; index++)
                {
                    WriteGuid(writer, result.ContainerRevisions[index].ContainerId);
                    writer.Write(result.ContainerRevisions[index].Revision);
                }

                writer.Write((byte)result.RecoveryDeliveryIds.Length);
                for (var index = 0; index < result.RecoveryDeliveryIds.Length; index++)
                {
                    WriteGuid(writer, result.RecoveryDeliveryIds[index]);
                }
            });
        }

        public static bool TryDecodeItemOperationResult(
            byte[] data,
            out RealtimeItemOperationResult result,
            out string error)
        {
            result = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.ItemOperationResult,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadGuid(reader, out var operationId, out error)
                    || !TryReadByte(reader, out var rawOperationKind, out error)
                    || !TryReadBoolean(reader, out var succeeded, out error)
                    || !TryReadBoolean(reader, out var requiresRefresh, out error)
                    || !TryReadCarryState(reader, out var carryState, out error))
                {
                    return false;
                }

                RealtimeError operationError = null;
                if (!succeeded)
                {
                    if (!TryReadString(
                            reader,
                            MaximumErrorCodeLength,
                            out var errorCode,
                            out error)
                        || !TryReadString(
                            reader,
                            MaximumErrorMessageLength,
                            out var errorMessage,
                            out error))
                    {
                        return false;
                    }

                    operationError = new RealtimeError(errorCode, errorMessage);
                }

                if (!TryReadByte(reader, out var itemRevisionCount, out error)
                    || itemRevisionCount > MaximumItemResultItemRevisions)
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Item result revision count is invalid."
                        : error;
                    return false;
                }

                var itemRevisions = new RealtimeItemRevision[itemRevisionCount];
                for (var index = 0; index < itemRevisionCount; index++)
                {
                    if (!TryReadItemExpectation(
                            reader,
                            out var itemId,
                            out var revision,
                            out error))
                    {
                        return false;
                    }

                    itemRevisions[index] = new RealtimeItemRevision(itemId, revision);
                }

                if (!TryReadByte(reader, out var containerRevisionCount, out error)
                    || containerRevisionCount > MaximumItemResultContainerRevisions)
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Item result container count is invalid."
                        : error;
                    return false;
                }

                var containerRevisions = new RealtimeContainerRevision[containerRevisionCount];
                for (var index = 0; index < containerRevisionCount; index++)
                {
                    if (!TryReadGuid(reader, out var containerId, out error)
                        || !TryReadInt64(reader, out var revision, out error))
                    {
                        return false;
                    }

                    containerRevisions[index] = new RealtimeContainerRevision(
                        containerId,
                        revision);
                }

                if (!TryReadByte(reader, out var deliveryCount, out error)
                    || deliveryCount > MaximumItemResultRecoveryDeliveries)
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Item result Recovery delivery count is invalid."
                        : error;
                    return false;
                }

                var recoveryDeliveryIds = new Guid[deliveryCount];
                for (var index = 0; index < deliveryCount; index++)
                {
                    if (!TryReadGuid(reader, out recoveryDeliveryIds[index], out error))
                    {
                        return false;
                    }
                }

                var decoded = new RealtimeItemOperationResult(
                    operationId,
                    (RealtimeItemOperationKind)rawOperationKind,
                    succeeded,
                    requiresRefresh,
                    operationError,
                    carryState,
                    itemRevisions,
                    containerRevisions,
                    recoveryDeliveryIds);
                if (!IsValidItemOperationResult(decoded) || !TryFinish(stream, out error))
                {
                    if (string.IsNullOrEmpty(error))
                    {
                        error = "Item operation result values are invalid.";
                    }

                    return false;
                }

                result = decoded;
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

        private static void WriteGuid(BinaryWriter writer, Guid value)
        {
            writer.Write(value.ToByteArray());
        }

        private static bool TryReadGuid(BinaryReader reader, out Guid value, out string error)
        {
            try
            {
                var bytes = reader.ReadBytes(16);
                if (bytes.Length != 16)
                {
                    value = Guid.Empty;
                    error = "Packet identifier is incomplete.";
                    return false;
                }

                value = new Guid(bytes);
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = Guid.Empty;
                error = "Packet identifier is incomplete.";
                return false;
            }
        }

        private static void WriteItemExpectation(
            BinaryWriter writer,
            Guid itemInstanceId,
            long revision)
        {
            WriteGuid(writer, itemInstanceId);
            writer.Write(revision);
        }

        private static bool TryReadItemExpectation(
            BinaryReader reader,
            out Guid itemInstanceId,
            out long revision,
            out string error)
        {
            revision = 0;
            return TryReadGuid(reader, out itemInstanceId, out error)
                && TryReadInt64(reader, out revision, out error);
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

        private static void WriteCarryState(BinaryWriter writer, RealtimeCarryState carryState)
        {
            if (!IsValidCarryState(carryState))
            {
                throw new ArgumentException("Carry state is invalid.", nameof(carryState));
            }

            writer.Write(carryState.ItemStateRevision);
            writer.Write(carryState.CarriedWeight);
            writer.Write(carryState.CarryCapacity);
        }

        private static bool TryReadCarryState(
            BinaryReader reader,
            out RealtimeCarryState carryState,
            out string error)
        {
            carryState = null;
            if (!TryReadInt64(reader, out var itemStateRevision, out error)
                || !TryReadInt64(reader, out var carriedWeight, out error)
                || !TryReadInt64(reader, out var carryCapacity, out error))
            {
                return false;
            }

            var decoded = new RealtimeCarryState(
                itemStateRevision,
                carriedWeight,
                carryCapacity);
            if (!IsValidCarryState(decoded))
            {
                error = "Carry state is invalid.";
                return false;
            }

            carryState = decoded;
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

        private static bool TryReadInt32(BinaryReader reader, out int value, out string error)
        {
            try
            {
                value = reader.ReadInt32();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet integer is incomplete.";
                return false;
            }
        }

        private static bool TryReadInt64(BinaryReader reader, out long value, out string error)
        {
            try
            {
                value = reader.ReadInt64();
                error = string.Empty;
                return true;
            }
            catch (EndOfStreamException)
            {
                value = 0;
                error = "Packet integer is incomplete.";
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

        private static bool IsValidItemOperationIntent(RealtimeItemOperationIntent intent)
        {
            if (intent == null
                || intent.OperationId == Guid.Empty
                || intent.ExpectedCharacterRevision < 0
                || !Enum.IsDefined(typeof(RealtimeItemOperationKind), intent.OperationKind))
            {
                return false;
            }

            switch (intent.OperationKind)
            {
                case RealtimeItemOperationKind.Relocate:
                case RealtimeItemOperationKind.Unequip:
                    return IsValidItemExpectation(
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision)
                        && intent.DestinationContainerId != Guid.Empty
                        && IsValidDestinationSlot(intent.DestinationSlotIndex);
                case RealtimeItemOperationKind.Equip:
                    return IsValidItemExpectation(
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision)
                        && !string.IsNullOrWhiteSpace(intent.EquipmentSlotId)
                        && intent.EquipmentSlotId.Length <= MaximumEquipmentSlotIdLength;
                case RealtimeItemOperationKind.SplitStack:
                    return IsValidItemExpectation(
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision)
                        && intent.Quantity > 0
                        && intent.DestinationContainerId != Guid.Empty
                        && IsValidDestinationSlot(intent.DestinationSlotIndex);
                case RealtimeItemOperationKind.MergeStacks:
                case RealtimeItemOperationKind.SwapContainerItems:
                    return IsValidItemExpectation(
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision)
                        && IsValidItemExpectation(
                            intent.TargetItemInstanceId,
                            intent.ExpectedTargetItemRevision)
                        && intent.ItemInstanceId != intent.TargetItemInstanceId;
                case RealtimeItemOperationKind.Destroy:
                    return IsValidItemExpectation(
                        intent.ItemInstanceId,
                        intent.ExpectedItemRevision);
                case RealtimeItemOperationKind.ClaimRecoveryDelivery:
                    return intent.RecoveryDeliveryId != Guid.Empty
                        && intent.ExpectedRecoveryDeliveryRevision >= 0
                        && intent.DestinationContainerId != Guid.Empty
                        && intent.Items != null
                        && intent.Items.Length > 0
                        && intent.Items.Length <= MaximumItemOperationItems
                        && Array.TrueForAll(
                            intent.Items,
                            item => item != null
                                && IsValidItemExpectation(
                                    item.ItemInstanceId,
                                    item.Revision));
                default:
                    return false;
            }
        }

        private static bool IsValidItemOperationResult(RealtimeItemOperationResult result)
        {
            if (result == null
                || result.OperationId == Guid.Empty
                || !Enum.IsDefined(typeof(RealtimeItemOperationKind), result.OperationKind)
                || !IsValidCarryState(result.CarryState)
                || result.ItemRevisions == null
                || result.ItemRevisions.Length > MaximumItemResultItemRevisions
                || result.ContainerRevisions == null
                || result.ContainerRevisions.Length > MaximumItemResultContainerRevisions
                || result.RecoveryDeliveryIds == null
                || result.RecoveryDeliveryIds.Length > MaximumItemResultRecoveryDeliveries)
            {
                return false;
            }

            if (result.Succeeded != (result.Error == null))
            {
                return false;
            }

            if (!result.Succeeded
                && (string.IsNullOrWhiteSpace(result.Error.Code)
                    || result.Error.Code.Length > MaximumErrorCodeLength
                    || string.IsNullOrWhiteSpace(result.Error.Message)
                    || result.Error.Message.Length > MaximumErrorMessageLength
                    || result.ItemRevisions.Length != 0
                    || result.ContainerRevisions.Length != 0
                    || result.RecoveryDeliveryIds.Length != 0))
            {
                return false;
            }

            return Array.TrueForAll(
                    result.ItemRevisions,
                    revision => revision != null
                        && IsValidItemExpectation(
                            revision.ItemInstanceId,
                            revision.Revision))
                && Array.TrueForAll(
                    result.ContainerRevisions,
                    revision => revision != null
                        && revision.ContainerId != Guid.Empty
                        && revision.Revision >= 0)
                && Array.TrueForAll(
                    result.RecoveryDeliveryIds,
                    deliveryId => deliveryId != Guid.Empty);
        }

        private static bool IsValidItemExpectation(Guid itemInstanceId, long revision)
        {
            return itemInstanceId != Guid.Empty && revision >= 0;
        }

        private static bool IsValidDestinationSlot(int slotIndex)
        {
            return slotIndex >= -1;
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

        private static bool IsValidCarryState(RealtimeCarryState carryState)
        {
            if (carryState == null
                || carryState.ItemStateRevision < 0
                || carryState.CarriedWeight < 0
                || carryState.CarryCapacity <= 0)
            {
                return false;
            }

            var extraCapacity = ((carryState.CarryCapacity / 5) * 2)
                + (((carryState.CarryCapacity % 5) * 2) / 5);
            return carryState.CarryCapacity > long.MaxValue - extraCapacity
                || carryState.CarriedWeight <= carryState.CarryCapacity + extraCapacity;
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
