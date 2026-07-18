using System;
using System.IO;

namespace ShooterMmo.GameProtocol
{
    public enum RealtimeWorldActorKind : byte
    {
        Npc = 1,
        Mob = 2
    }

    public enum RealtimeWorldActorDisposition : byte
    {
        Friendly = 1,
        Neutral = 2,
        Hostile = 3
    }

    public enum RealtimeWorldActorActivityTier : byte
    {
        EventDriven = 1,
        Dormant = 2,
        Active = 3
    }

    public enum RealtimeWorldInteractionTargetKind : byte
    {
        WorldActor = 1,
        Corpse = 2
    }

    public enum RealtimeWorldInteractionOperationKind : byte
    {
        Open = 1,
        Close = 2,
        CapabilityAction = 3
    }

    public enum RealtimeWorldActorCapabilityKind : byte
    {
        Dialogue = 1,
        Vendor = 2,
        QuestOffer = 3,
        QuestTurnIn = 4,
        Crafting = 5,
        Insurance = 6,
        Trainer = 7,
        Bank = 8,
        RecoveryStorage = 9
    }

    public sealed class RealtimeWorldActorSpawn
    {
        public RealtimeWorldActorSpawn(
            ulong entityId,
            Guid runtimeActorId,
            string actorDefinitionId,
            string spawnDefinitionId,
            string displayName,
            RealtimeWorldActorKind kind,
            string factionId,
            RealtimeWorldActorDisposition disposition,
            string presentationArchetypeId,
            float positionX,
            float positionY,
            float positionZ,
            float yawDegrees,
            long stateRevision,
            long interactionRevision,
            bool isActive,
            float boundsCenterX,
            float boundsCenterY,
            float boundsCenterZ,
            float boundsSizeX,
            float boundsSizeY,
            float boundsSizeZ,
            RealtimeWorldActorActivityTier activityTier)
        {
            EntityId = entityId;
            RuntimeActorId = runtimeActorId;
            ActorDefinitionId = actorDefinitionId;
            SpawnDefinitionId = spawnDefinitionId;
            DisplayName = displayName;
            Kind = kind;
            FactionId = factionId;
            Disposition = disposition;
            PresentationArchetypeId = presentationArchetypeId;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            YawDegrees = yawDegrees;
            StateRevision = stateRevision;
            InteractionRevision = interactionRevision;
            IsActive = isActive;
            BoundsCenterX = boundsCenterX;
            BoundsCenterY = boundsCenterY;
            BoundsCenterZ = boundsCenterZ;
            BoundsSizeX = boundsSizeX;
            BoundsSizeY = boundsSizeY;
            BoundsSizeZ = boundsSizeZ;
            ActivityTier = activityTier;
        }

        public ulong EntityId { get; }
        public Guid RuntimeActorId { get; }
        public string ActorDefinitionId { get; }
        public string SpawnDefinitionId { get; }
        public string DisplayName { get; }
        public RealtimeWorldActorKind Kind { get; }
        public string FactionId { get; }
        public RealtimeWorldActorDisposition Disposition { get; }
        public string PresentationArchetypeId { get; }
        public float PositionX { get; }
        public float PositionY { get; }
        public float PositionZ { get; }
        public float YawDegrees { get; }
        public long StateRevision { get; }
        public long InteractionRevision { get; }
        public bool IsActive { get; }
        public float BoundsCenterX { get; }
        public float BoundsCenterY { get; }
        public float BoundsCenterZ { get; }
        public float BoundsSizeX { get; }
        public float BoundsSizeY { get; }
        public float BoundsSizeZ { get; }
        public RealtimeWorldActorActivityTier ActivityTier { get; }
    }

    public sealed class RealtimeWorldActorState
    {
        public RealtimeWorldActorState(
            ulong entityId,
            long stateRevision,
            long interactionRevision,
            bool isActive,
            float positionX,
            float positionY,
            float positionZ,
            float yawDegrees,
            RealtimeWorldActorActivityTier activityTier)
        {
            EntityId = entityId;
            StateRevision = stateRevision;
            InteractionRevision = interactionRevision;
            IsActive = isActive;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            YawDegrees = yawDegrees;
            ActivityTier = activityTier;
        }

        public ulong EntityId { get; }
        public long StateRevision { get; }
        public long InteractionRevision { get; }
        public bool IsActive { get; }
        public float PositionX { get; }
        public float PositionY { get; }
        public float PositionZ { get; }
        public float YawDegrees { get; }
        public RealtimeWorldActorActivityTier ActivityTier { get; }
    }

    public sealed class RealtimeWorldActorDespawn
    {
        public RealtimeWorldActorDespawn(ulong entityId, Guid runtimeActorId, string reason)
        {
            EntityId = entityId;
            RuntimeActorId = runtimeActorId;
            Reason = reason;
        }

        public ulong EntityId { get; }
        public Guid RuntimeActorId { get; }
        public string Reason { get; }
    }

    public sealed class RealtimeWorldInteractionIntent
    {
        private RealtimeWorldInteractionIntent(
            Guid operationId,
            Guid simulationSessionId,
            Guid interactionSessionId,
            RealtimeWorldInteractionOperationKind operationKind,
            RealtimeWorldInteractionTargetKind targetKind,
            ulong targetEntityId,
            Guid targetRuntimeId,
            long expectedTargetRevision,
            string capabilityId,
            RealtimeWorldActorCapabilityKind capabilityKind,
            long expectedCapabilityRevision)
        {
            OperationId = operationId;
            SimulationSessionId = simulationSessionId;
            InteractionSessionId = interactionSessionId;
            OperationKind = operationKind;
            TargetKind = targetKind;
            TargetEntityId = targetEntityId;
            TargetRuntimeId = targetRuntimeId;
            ExpectedTargetRevision = expectedTargetRevision;
            CapabilityId = capabilityId;
            CapabilityKind = capabilityKind;
            ExpectedCapabilityRevision = expectedCapabilityRevision;
        }

        public Guid OperationId { get; }
        public Guid SimulationSessionId { get; }
        public Guid InteractionSessionId { get; }
        public RealtimeWorldInteractionOperationKind OperationKind { get; }
        public RealtimeWorldInteractionTargetKind TargetKind { get; }
        public ulong TargetEntityId { get; }
        public Guid TargetRuntimeId { get; }
        public long ExpectedTargetRevision { get; }
        public string CapabilityId { get; }
        public RealtimeWorldActorCapabilityKind CapabilityKind { get; }
        public long ExpectedCapabilityRevision { get; }

        public static RealtimeWorldInteractionIntent CreateOpen(
            Guid operationId,
            Guid simulationSessionId,
            RealtimeWorldInteractionTargetKind targetKind,
            ulong targetEntityId,
            Guid targetRuntimeId,
            long expectedTargetRevision)
        {
            return new RealtimeWorldInteractionIntent(
                operationId,
                simulationSessionId,
                Guid.Empty,
                RealtimeWorldInteractionOperationKind.Open,
                targetKind,
                targetEntityId,
                targetRuntimeId,
                expectedTargetRevision,
                string.Empty,
                default,
                0);
        }

        public static RealtimeWorldInteractionIntent CreateClose(
            Guid operationId,
            Guid simulationSessionId,
            Guid interactionSessionId,
            RealtimeWorldInteractionTargetKind targetKind,
            ulong targetEntityId,
            Guid targetRuntimeId,
            long expectedTargetRevision)
        {
            return new RealtimeWorldInteractionIntent(
                operationId,
                simulationSessionId,
                interactionSessionId,
                RealtimeWorldInteractionOperationKind.Close,
                targetKind,
                targetEntityId,
                targetRuntimeId,
                expectedTargetRevision,
                string.Empty,
                default,
                0);
        }

        public static RealtimeWorldInteractionIntent CreateCapabilityAction(
            Guid operationId,
            Guid simulationSessionId,
            Guid interactionSessionId,
            RealtimeWorldInteractionTargetKind targetKind,
            ulong targetEntityId,
            Guid targetRuntimeId,
            long expectedTargetRevision,
            string capabilityId,
            RealtimeWorldActorCapabilityKind capabilityKind,
            long expectedCapabilityRevision)
        {
            return new RealtimeWorldInteractionIntent(
                operationId,
                simulationSessionId,
                interactionSessionId,
                RealtimeWorldInteractionOperationKind.CapabilityAction,
                targetKind,
                targetEntityId,
                targetRuntimeId,
                expectedTargetRevision,
                capabilityId,
                capabilityKind,
                expectedCapabilityRevision);
        }
    }

    public sealed class RealtimeWorldActorCapability
    {
        public RealtimeWorldActorCapability(
            string id,
            RealtimeWorldActorCapabilityKind kind,
            string displayName,
            bool isAvailable,
            long revision)
        {
            Id = id;
            Kind = kind;
            DisplayName = displayName;
            IsAvailable = isAvailable;
            Revision = revision;
        }

        public string Id { get; }
        public RealtimeWorldActorCapabilityKind Kind { get; }
        public string DisplayName { get; }
        public bool IsAvailable { get; }
        public long Revision { get; }
    }

    public sealed class RealtimeWorldInteractionOpened
    {
        public RealtimeWorldInteractionOpened(
            Guid operationId,
            Guid interactionSessionId,
            RealtimeWorldInteractionTargetKind targetKind,
            ulong targetEntityId,
            Guid targetRuntimeId,
            long targetRevision,
            long capabilitySummaryRevision,
            string displayName,
            RealtimeWorldActorCapability[] capabilities)
        {
            OperationId = operationId;
            InteractionSessionId = interactionSessionId;
            TargetKind = targetKind;
            TargetEntityId = targetEntityId;
            TargetRuntimeId = targetRuntimeId;
            TargetRevision = targetRevision;
            CapabilitySummaryRevision = capabilitySummaryRevision;
            DisplayName = displayName;
            Capabilities = capabilities ?? Array.Empty<RealtimeWorldActorCapability>();
        }

        public Guid OperationId { get; }
        public Guid InteractionSessionId { get; }
        public RealtimeWorldInteractionTargetKind TargetKind { get; }
        public ulong TargetEntityId { get; }
        public Guid TargetRuntimeId { get; }
        public long TargetRevision { get; }
        public long CapabilitySummaryRevision { get; }
        public string DisplayName { get; }
        public RealtimeWorldActorCapability[] Capabilities { get; }
    }

    public sealed class RealtimeWorldInteractionResult
    {
        public RealtimeWorldInteractionResult(
            Guid operationId,
            Guid interactionSessionId,
            RealtimeWorldInteractionOperationKind operationKind,
            bool succeeded,
            long targetRevision,
            RealtimeError error)
        {
            OperationId = operationId;
            InteractionSessionId = interactionSessionId;
            OperationKind = operationKind;
            Succeeded = succeeded;
            TargetRevision = targetRevision;
            Error = error;
        }

        public Guid OperationId { get; }
        public Guid InteractionSessionId { get; }
        public RealtimeWorldInteractionOperationKind OperationKind { get; }
        public bool Succeeded { get; }
        public long TargetRevision { get; }
        public RealtimeError Error { get; }
    }

    public sealed class RealtimeWorldInteractionClosed
    {
        public RealtimeWorldInteractionClosed(
            Guid interactionSessionId,
            RealtimeWorldInteractionTargetKind targetKind,
            ulong targetEntityId,
            Guid targetRuntimeId,
            string code,
            string message)
        {
            InteractionSessionId = interactionSessionId;
            TargetKind = targetKind;
            TargetEntityId = targetEntityId;
            TargetRuntimeId = targetRuntimeId;
            Code = code;
            Message = message;
        }

        public Guid InteractionSessionId { get; }
        public RealtimeWorldInteractionTargetKind TargetKind { get; }
        public ulong TargetEntityId { get; }
        public Guid TargetRuntimeId { get; }
        public string Code { get; }
        public string Message { get; }
    }

    public static partial class RealtimeProtocol
    {
        private const int MaximumWorldActorCapabilities = 16;
        private const int MaximumWorldActorCapabilityIdLength = 128;
        private const int MaximumWorldActorCapabilityNameLength = 128;

        public static byte[] EncodeWorldActorSpawn(RealtimeWorldActorSpawn spawn)
        {
            if (!IsValidWorldActorSpawn(spawn))
            {
                throw new ArgumentException("World actor spawn is invalid.", nameof(spawn));
            }

            return Encode(RealtimeMessageType.WorldActorSpawn, writer =>
            {
                writer.Write(spawn.EntityId);
                WriteGuid(writer, spawn.RuntimeActorId);
                WriteString(writer, spawn.ActorDefinitionId, MaximumIdentifierLength, nameof(spawn.ActorDefinitionId));
                WriteString(writer, spawn.SpawnDefinitionId, MaximumIdentifierLength, nameof(spawn.SpawnDefinitionId));
                WriteString(writer, spawn.DisplayName, MaximumNameLength, nameof(spawn.DisplayName));
                writer.Write((byte)spawn.Kind);
                WriteString(writer, spawn.FactionId, MaximumIdentifierLength, nameof(spawn.FactionId));
                writer.Write((byte)spawn.Disposition);
                WriteString(writer, spawn.PresentationArchetypeId, MaximumIdentifierLength, nameof(spawn.PresentationArchetypeId));
                WriteWorldActorStatePayload(writer, spawn);
                writer.Write(spawn.BoundsCenterX);
                writer.Write(spawn.BoundsCenterY);
                writer.Write(spawn.BoundsCenterZ);
                writer.Write(spawn.BoundsSizeX);
                writer.Write(spawn.BoundsSizeY);
                writer.Write(spawn.BoundsSizeZ);
            });
        }

        public static bool TryDecodeWorldActorSpawn(
            byte[] data,
            out RealtimeWorldActorSpawn spawn,
            out string error)
        {
            spawn = null;
            if (!TryCreateReader(data, RealtimeMessageType.WorldActorSpawn, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadUInt64(reader, out var entityId, out error)
                    || !TryReadGuid(reader, out var runtimeActorId, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var actorDefinitionId, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var spawnDefinitionId, out error)
                    || !TryReadString(reader, MaximumNameLength, out var displayName, out error)
                    || !TryReadByte(reader, out var rawKind, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var factionId, out error)
                    || !TryReadByte(reader, out var rawDisposition, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var presentationId, out error)
                    || !TryReadWorldActorStatePayload(
                        reader,
                        out var stateRevision,
                        out var interactionRevision,
                        out var isActive,
                        out var positionX,
                        out var positionY,
                        out var positionZ,
                        out var yaw,
                        out var rawActivityTier,
                        out error)
                    || !TryReadSingle(reader, out var boundsCenterX, out error)
                    || !TryReadSingle(reader, out var boundsCenterY, out error)
                    || !TryReadSingle(reader, out var boundsCenterZ, out error)
                    || !TryReadSingle(reader, out var boundsSizeX, out error)
                    || !TryReadSingle(reader, out var boundsSizeY, out error)
                    || !TryReadSingle(reader, out var boundsSizeZ, out error)
                    || !TryFinish(stream, out error))
                {
                    return false;
                }

                var decoded = new RealtimeWorldActorSpawn(
                    entityId,
                    runtimeActorId,
                    actorDefinitionId,
                    spawnDefinitionId,
                    displayName,
                    (RealtimeWorldActorKind)rawKind,
                    factionId,
                    (RealtimeWorldActorDisposition)rawDisposition,
                    presentationId,
                    positionX,
                    positionY,
                    positionZ,
                    yaw,
                    stateRevision,
                    interactionRevision,
                    isActive,
                    boundsCenterX,
                    boundsCenterY,
                    boundsCenterZ,
                    boundsSizeX,
                    boundsSizeY,
                    boundsSizeZ,
                    (RealtimeWorldActorActivityTier)rawActivityTier);
                if (!IsValidWorldActorSpawn(decoded))
                {
                    error = "World actor spawn is invalid.";
                    return false;
                }

                spawn = decoded;
                return true;
            }
        }

        public static byte[] EncodeWorldActorState(RealtimeWorldActorState state)
        {
            if (!IsValidWorldActorState(state))
            {
                throw new ArgumentException("World actor state is invalid.", nameof(state));
            }

            return Encode(RealtimeMessageType.WorldActorState, writer =>
            {
                writer.Write(state.EntityId);
                WriteWorldActorStatePayload(writer, state);
            });
        }

        public static bool TryDecodeWorldActorState(
            byte[] data,
            out RealtimeWorldActorState state,
            out string error)
        {
            state = null;
            if (!TryCreateReader(data, RealtimeMessageType.WorldActorState, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadUInt64(reader, out var entityId, out error)
                    || !TryReadWorldActorStatePayload(
                        reader,
                        out var stateRevision,
                        out var interactionRevision,
                        out var isActive,
                        out var positionX,
                        out var positionY,
                        out var positionZ,
                        out var yaw,
                        out var rawActivityTier,
                        out error)
                    || !TryFinish(stream, out error))
                {
                    return false;
                }

                var decoded = new RealtimeWorldActorState(
                    entityId,
                    stateRevision,
                    interactionRevision,
                    isActive,
                    positionX,
                    positionY,
                    positionZ,
                    yaw,
                    (RealtimeWorldActorActivityTier)rawActivityTier);
                if (!IsValidWorldActorState(decoded))
                {
                    error = "World actor state is invalid.";
                    return false;
                }

                state = decoded;
                return true;
            }
        }

        public static byte[] EncodeWorldActorDespawn(RealtimeWorldActorDespawn despawn)
        {
            if (despawn == null
                || despawn.EntityId == 0
                || despawn.RuntimeActorId == Guid.Empty
                || string.IsNullOrWhiteSpace(despawn.Reason))
            {
                throw new ArgumentException("World actor despawn is invalid.", nameof(despawn));
            }

            return Encode(RealtimeMessageType.WorldActorDespawn, writer =>
            {
                writer.Write(despawn.EntityId);
                WriteGuid(writer, despawn.RuntimeActorId);
                WriteString(writer, despawn.Reason, MaximumErrorCodeLength, nameof(despawn.Reason));
            });
        }

        public static bool TryDecodeWorldActorDespawn(
            byte[] data,
            out RealtimeWorldActorDespawn despawn,
            out string error)
        {
            despawn = null;
            if (!TryCreateReader(data, RealtimeMessageType.WorldActorDespawn, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadUInt64(reader, out var entityId, out error)
                    || !TryReadGuid(reader, out var runtimeActorId, out error)
                    || !TryReadString(reader, MaximumErrorCodeLength, out var reason, out error)
                    || !TryFinish(stream, out error)
                    || entityId == 0
                    || runtimeActorId == Guid.Empty
                    || string.IsNullOrWhiteSpace(reason))
                {
                    error = string.IsNullOrEmpty(error) ? "World actor despawn is invalid." : error;
                    return false;
                }

                despawn = new RealtimeWorldActorDespawn(entityId, runtimeActorId, reason);
                return true;
            }
        }

        public static byte[] EncodeWorldInteractionIntent(RealtimeWorldInteractionIntent intent)
        {
            if (!IsValidWorldInteractionIntent(intent))
            {
                throw new ArgumentException("World interaction intent is invalid.", nameof(intent));
            }

            return Encode(RealtimeMessageType.WorldInteractionIntent, writer =>
            {
                WriteGuid(writer, intent.OperationId);
                WriteGuid(writer, intent.SimulationSessionId);
                WriteGuid(writer, intent.InteractionSessionId);
                writer.Write((byte)intent.OperationKind);
                writer.Write((byte)intent.TargetKind);
                writer.Write(intent.TargetEntityId);
                WriteGuid(writer, intent.TargetRuntimeId);
                writer.Write(intent.ExpectedTargetRevision);
                if (intent.OperationKind == RealtimeWorldInteractionOperationKind.CapabilityAction)
                {
                    WriteString(writer, intent.CapabilityId, MaximumWorldActorCapabilityIdLength, nameof(intent.CapabilityId));
                    writer.Write((byte)intent.CapabilityKind);
                    writer.Write(intent.ExpectedCapabilityRevision);
                }
            });
        }

        public static bool TryDecodeWorldInteractionIntent(
            byte[] data,
            out RealtimeWorldInteractionIntent intent,
            out string error)
        {
            intent = null;
            if (!TryCreateReader(data, RealtimeMessageType.WorldInteractionIntent, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadGuid(reader, out var operationId, out error)
                    || !TryReadGuid(reader, out var simulationSessionId, out error)
                    || !TryReadGuid(reader, out var interactionSessionId, out error)
                    || !TryReadByte(reader, out var rawOperationKind, out error)
                    || !TryReadByte(reader, out var rawTargetKind, out error)
                    || !TryReadUInt64(reader, out var targetEntityId, out error)
                    || !TryReadGuid(reader, out var targetRuntimeId, out error)
                    || !TryReadInt64(reader, out var expectedTargetRevision, out error))
                {
                    return false;
                }

                var operationKind = (RealtimeWorldInteractionOperationKind)rawOperationKind;
                var capabilityId = string.Empty;
                var capabilityKind = default(RealtimeWorldActorCapabilityKind);
                var expectedCapabilityRevision = 0L;
                var rawCapabilityKind = (byte)0;
                if (operationKind == RealtimeWorldInteractionOperationKind.CapabilityAction
                    && (!TryReadString(
                            reader,
                            MaximumWorldActorCapabilityIdLength,
                            out capabilityId,
                            out error)
                        || !TryReadByte(reader, out rawCapabilityKind, out error)
                        || !TryReadInt64(
                            reader,
                            out expectedCapabilityRevision,
                            out error)))
                {
                    return false;
                }

                if (operationKind == RealtimeWorldInteractionOperationKind.CapabilityAction)
                {
                    capabilityKind = (RealtimeWorldActorCapabilityKind)rawCapabilityKind;
                }

                if (!TryFinish(stream, out error))
                {
                    return false;
                }

                var targetKind = (RealtimeWorldInteractionTargetKind)rawTargetKind;
                RealtimeWorldInteractionIntent decoded;
                if (operationKind == RealtimeWorldInteractionOperationKind.Open)
                {
                    decoded = RealtimeWorldInteractionIntent.CreateOpen(
                        operationId,
                        simulationSessionId,
                        targetKind,
                        targetEntityId,
                        targetRuntimeId,
                        expectedTargetRevision);
                }
                else if (operationKind == RealtimeWorldInteractionOperationKind.Close)
                {
                    decoded = RealtimeWorldInteractionIntent.CreateClose(
                        operationId,
                        simulationSessionId,
                        interactionSessionId,
                        targetKind,
                        targetEntityId,
                        targetRuntimeId,
                        expectedTargetRevision);
                }
                else
                {
                    decoded = RealtimeWorldInteractionIntent.CreateCapabilityAction(
                        operationId,
                        simulationSessionId,
                        interactionSessionId,
                        targetKind,
                        targetEntityId,
                        targetRuntimeId,
                        expectedTargetRevision,
                        capabilityId,
                        capabilityKind,
                        expectedCapabilityRevision);
                }

                if (!IsValidWorldInteractionIntent(decoded))
                {
                    error = "World interaction intent is invalid.";
                    return false;
                }

                intent = decoded;
                return true;
            }
        }

        public static byte[] EncodeWorldInteractionOpened(RealtimeWorldInteractionOpened opened)
        {
            if (!IsValidWorldInteractionOpened(opened))
            {
                throw new ArgumentException("World interaction opened state is invalid.", nameof(opened));
            }

            return Encode(RealtimeMessageType.WorldInteractionOpened, writer =>
            {
                WriteGuid(writer, opened.OperationId);
                WriteGuid(writer, opened.InteractionSessionId);
                writer.Write((byte)opened.TargetKind);
                writer.Write(opened.TargetEntityId);
                WriteGuid(writer, opened.TargetRuntimeId);
                writer.Write(opened.TargetRevision);
                writer.Write(opened.CapabilitySummaryRevision);
                WriteString(writer, opened.DisplayName, MaximumNameLength, nameof(opened.DisplayName));
                writer.Write((byte)opened.Capabilities.Length);
                for (var index = 0; index < opened.Capabilities.Length; index++)
                {
                    WriteWorldActorCapability(writer, opened.Capabilities[index]);
                }
            });
        }

        public static bool TryDecodeWorldInteractionOpened(
            byte[] data,
            out RealtimeWorldInteractionOpened opened,
            out string error)
        {
            opened = null;
            if (!TryCreateReader(data, RealtimeMessageType.WorldInteractionOpened, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadGuid(reader, out var operationId, out error)
                    || !TryReadGuid(reader, out var interactionSessionId, out error)
                    || !TryReadByte(reader, out var rawTargetKind, out error)
                    || !TryReadUInt64(reader, out var targetEntityId, out error)
                    || !TryReadGuid(reader, out var targetRuntimeId, out error)
                    || !TryReadInt64(reader, out var targetRevision, out error)
                    || !TryReadInt64(reader, out var capabilitySummaryRevision, out error)
                    || !TryReadString(reader, MaximumNameLength, out var displayName, out error)
                    || !TryReadByte(reader, out var capabilityCount, out error)
                    || capabilityCount > MaximumWorldActorCapabilities)
                {
                    error = string.IsNullOrEmpty(error) ? "World interaction capability count is invalid." : error;
                    return false;
                }

                var capabilities = new RealtimeWorldActorCapability[capabilityCount];
                for (var index = 0; index < capabilities.Length; index++)
                {
                    if (!TryReadWorldActorCapability(reader, out capabilities[index], out error))
                    {
                        return false;
                    }
                }

                if (!TryFinish(stream, out error))
                {
                    return false;
                }

                var decoded = new RealtimeWorldInteractionOpened(
                    operationId,
                    interactionSessionId,
                    (RealtimeWorldInteractionTargetKind)rawTargetKind,
                    targetEntityId,
                    targetRuntimeId,
                    targetRevision,
                    capabilitySummaryRevision,
                    displayName,
                    capabilities);
                if (!IsValidWorldInteractionOpened(decoded))
                {
                    error = "World interaction opened state is invalid.";
                    return false;
                }

                opened = decoded;
                return true;
            }
        }

        public static byte[] EncodeWorldInteractionResult(RealtimeWorldInteractionResult result)
        {
            if (!IsValidWorldInteractionResult(result))
            {
                throw new ArgumentException("World interaction result is invalid.", nameof(result));
            }

            return Encode(RealtimeMessageType.WorldInteractionResult, writer =>
            {
                WriteGuid(writer, result.OperationId);
                WriteGuid(writer, result.InteractionSessionId);
                writer.Write((byte)result.OperationKind);
                writer.Write(result.Succeeded);
                writer.Write(result.TargetRevision);
                if (!result.Succeeded)
                {
                    WriteString(writer, result.Error.Code, MaximumErrorCodeLength, nameof(result.Error.Code));
                    WriteString(writer, result.Error.Message, MaximumErrorMessageLength, nameof(result.Error.Message));
                }
            });
        }

        public static bool TryDecodeWorldInteractionResult(
            byte[] data,
            out RealtimeWorldInteractionResult result,
            out string error)
        {
            result = null;
            if (!TryCreateReader(data, RealtimeMessageType.WorldInteractionResult, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadGuid(reader, out var operationId, out error)
                    || !TryReadGuid(reader, out var interactionSessionId, out error)
                    || !TryReadByte(reader, out var rawOperationKind, out error)
                    || !TryReadBoolean(reader, out var succeeded, out error)
                    || !TryReadInt64(reader, out var targetRevision, out error))
                {
                    return false;
                }

                RealtimeError resultError = null;
                if (!succeeded)
                {
                    if (!TryReadString(reader, MaximumErrorCodeLength, out var code, out error)
                        || !TryReadString(reader, MaximumErrorMessageLength, out var message, out error))
                    {
                        return false;
                    }

                    resultError = new RealtimeError(code, message);
                }

                if (!TryFinish(stream, out error))
                {
                    return false;
                }

                var decoded = new RealtimeWorldInteractionResult(
                    operationId,
                    interactionSessionId,
                    (RealtimeWorldInteractionOperationKind)rawOperationKind,
                    succeeded,
                    targetRevision,
                    resultError);
                if (!IsValidWorldInteractionResult(decoded))
                {
                    error = "World interaction result is invalid.";
                    return false;
                }

                result = decoded;
                return true;
            }
        }

        public static byte[] EncodeWorldInteractionClosed(RealtimeWorldInteractionClosed closed)
        {
            if (!IsValidWorldInteractionClosed(closed))
            {
                throw new ArgumentException("World interaction closure is invalid.", nameof(closed));
            }

            return Encode(RealtimeMessageType.WorldInteractionClosed, writer =>
            {
                WriteGuid(writer, closed.InteractionSessionId);
                writer.Write((byte)closed.TargetKind);
                writer.Write(closed.TargetEntityId);
                WriteGuid(writer, closed.TargetRuntimeId);
                WriteString(writer, closed.Code, MaximumErrorCodeLength, nameof(closed.Code));
                WriteString(writer, closed.Message, MaximumErrorMessageLength, nameof(closed.Message));
            });
        }

        public static bool TryDecodeWorldInteractionClosed(
            byte[] data,
            out RealtimeWorldInteractionClosed closed,
            out string error)
        {
            closed = null;
            if (!TryCreateReader(data, RealtimeMessageType.WorldInteractionClosed, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadGuid(reader, out var interactionSessionId, out error)
                    || !TryReadByte(reader, out var rawTargetKind, out error)
                    || !TryReadUInt64(reader, out var targetEntityId, out error)
                    || !TryReadGuid(reader, out var targetRuntimeId, out error)
                    || !TryReadString(reader, MaximumErrorCodeLength, out var code, out error)
                    || !TryReadString(reader, MaximumErrorMessageLength, out var message, out error)
                    || !TryFinish(stream, out error))
                {
                    return false;
                }

                var decoded = new RealtimeWorldInteractionClosed(
                    interactionSessionId,
                    (RealtimeWorldInteractionTargetKind)rawTargetKind,
                    targetEntityId,
                    targetRuntimeId,
                    code,
                    message);
                if (!IsValidWorldInteractionClosed(decoded))
                {
                    error = "World interaction closure is invalid.";
                    return false;
                }

                closed = decoded;
                return true;
            }
        }

        private static void WriteWorldActorStatePayload(BinaryWriter writer, RealtimeWorldActorSpawn spawn)
        {
            writer.Write(spawn.StateRevision);
            writer.Write(spawn.InteractionRevision);
            writer.Write(spawn.IsActive);
            writer.Write(spawn.PositionX);
            writer.Write(spawn.PositionY);
            writer.Write(spawn.PositionZ);
            writer.Write(spawn.YawDegrees);
            writer.Write((byte)spawn.ActivityTier);
        }

        private static void WriteWorldActorStatePayload(BinaryWriter writer, RealtimeWorldActorState state)
        {
            writer.Write(state.StateRevision);
            writer.Write(state.InteractionRevision);
            writer.Write(state.IsActive);
            writer.Write(state.PositionX);
            writer.Write(state.PositionY);
            writer.Write(state.PositionZ);
            writer.Write(state.YawDegrees);
            writer.Write((byte)state.ActivityTier);
        }

        private static bool TryReadWorldActorStatePayload(
            BinaryReader reader,
            out long stateRevision,
            out long interactionRevision,
            out bool isActive,
            out float positionX,
            out float positionY,
            out float positionZ,
            out float yawDegrees,
            out byte activityTier,
            out string error)
        {
            stateRevision = 0;
            interactionRevision = 0;
            isActive = false;
            positionX = 0f;
            positionY = 0f;
            positionZ = 0f;
            yawDegrees = 0f;
            activityTier = 0;
            return TryReadInt64(reader, out stateRevision, out error)
                && TryReadInt64(reader, out interactionRevision, out error)
                && TryReadBoolean(reader, out isActive, out error)
                && TryReadSingle(reader, out positionX, out error)
                && TryReadSingle(reader, out positionY, out error)
                && TryReadSingle(reader, out positionZ, out error)
                && TryReadSingle(reader, out yawDegrees, out error)
                && TryReadByte(reader, out activityTier, out error);
        }

        private static void WriteWorldActorCapability(
            BinaryWriter writer,
            RealtimeWorldActorCapability capability)
        {
            WriteString(writer, capability.Id, MaximumWorldActorCapabilityIdLength, nameof(capability.Id));
            writer.Write((byte)capability.Kind);
            WriteString(writer, capability.DisplayName, MaximumWorldActorCapabilityNameLength, nameof(capability.DisplayName));
            writer.Write(capability.IsAvailable);
            writer.Write(capability.Revision);
        }

        private static bool TryReadWorldActorCapability(
            BinaryReader reader,
            out RealtimeWorldActorCapability capability,
            out string error)
        {
            capability = null;
            if (!TryReadString(reader, MaximumWorldActorCapabilityIdLength, out var id, out error)
                || !TryReadByte(reader, out var rawKind, out error)
                || !TryReadString(reader, MaximumWorldActorCapabilityNameLength, out var displayName, out error)
                || !TryReadBoolean(reader, out var isAvailable, out error)
                || !TryReadInt64(reader, out var revision, out error))
            {
                return false;
            }

            var decoded = new RealtimeWorldActorCapability(
                id,
                (RealtimeWorldActorCapabilityKind)rawKind,
                displayName,
                isAvailable,
                revision);
            if (!IsValidWorldActorCapability(decoded))
            {
                error = "World actor capability is invalid.";
                return false;
            }

            capability = decoded;
            return true;
        }

        private static bool IsValidWorldActorSpawn(RealtimeWorldActorSpawn spawn)
        {
            return spawn != null
                && spawn.EntityId != 0
                && spawn.RuntimeActorId != Guid.Empty
                && IsIdentifier(spawn.ActorDefinitionId)
                && IsIdentifier(spawn.SpawnDefinitionId)
                && IsName(spawn.DisplayName)
                && Enum.IsDefined(typeof(RealtimeWorldActorKind), spawn.Kind)
                && IsIdentifier(spawn.FactionId)
                && Enum.IsDefined(typeof(RealtimeWorldActorDisposition), spawn.Disposition)
                && IsIdentifier(spawn.PresentationArchetypeId)
                && spawn.StateRevision > 0
                && spawn.InteractionRevision > 0
                && IsFinite(spawn.PositionX)
                && IsFinite(spawn.PositionY)
                && IsFinite(spawn.PositionZ)
                && IsFinite(spawn.YawDegrees)
                && IsFinite(spawn.BoundsCenterX)
                && IsFinite(spawn.BoundsCenterY)
                && IsFinite(spawn.BoundsCenterZ)
                && IsFinite(spawn.BoundsSizeX)
                && spawn.BoundsSizeX > 0f
                && IsFinite(spawn.BoundsSizeY)
                && spawn.BoundsSizeY > 0f
                && IsFinite(spawn.BoundsSizeZ)
                && spawn.BoundsSizeZ > 0f
                && Enum.IsDefined(typeof(RealtimeWorldActorActivityTier), spawn.ActivityTier);
        }

        private static bool IsValidWorldActorState(RealtimeWorldActorState state)
        {
            return state != null
                && state.EntityId != 0
                && state.StateRevision > 0
                && state.InteractionRevision > 0
                && IsFinite(state.PositionX)
                && IsFinite(state.PositionY)
                && IsFinite(state.PositionZ)
                && IsFinite(state.YawDegrees)
                && Enum.IsDefined(typeof(RealtimeWorldActorActivityTier), state.ActivityTier);
        }

        private static bool IsValidWorldInteractionIntent(RealtimeWorldInteractionIntent intent)
        {
            if (intent == null
                || intent.OperationId == Guid.Empty
                || intent.SimulationSessionId == Guid.Empty
                || !Enum.IsDefined(typeof(RealtimeWorldInteractionOperationKind), intent.OperationKind)
                || !Enum.IsDefined(typeof(RealtimeWorldInteractionTargetKind), intent.TargetKind)
                || intent.TargetEntityId == 0
                || intent.TargetRuntimeId == Guid.Empty
                || intent.ExpectedTargetRevision <= 0)
            {
                return false;
            }

            return intent.OperationKind switch
            {
                RealtimeWorldInteractionOperationKind.Open =>
                    intent.InteractionSessionId == Guid.Empty
                    && string.IsNullOrEmpty(intent.CapabilityId)
                    && intent.CapabilityKind == default
                    && intent.ExpectedCapabilityRevision == 0,
                RealtimeWorldInteractionOperationKind.Close =>
                    intent.InteractionSessionId != Guid.Empty
                    && string.IsNullOrEmpty(intent.CapabilityId)
                    && intent.CapabilityKind == default
                    && intent.ExpectedCapabilityRevision == 0,
                RealtimeWorldInteractionOperationKind.CapabilityAction =>
                    intent.InteractionSessionId != Guid.Empty
                    && IsCapabilityId(intent.CapabilityId)
                    && Enum.IsDefined(
                        typeof(RealtimeWorldActorCapabilityKind),
                        intent.CapabilityKind)
                    && intent.ExpectedCapabilityRevision > 0,
                _ => false
            };
        }

        private static bool IsValidWorldInteractionOpened(RealtimeWorldInteractionOpened opened)
        {
            if (opened == null
                || opened.OperationId == Guid.Empty
                || opened.InteractionSessionId == Guid.Empty
                || !Enum.IsDefined(typeof(RealtimeWorldInteractionTargetKind), opened.TargetKind)
                || opened.TargetEntityId == 0
                || opened.TargetRuntimeId == Guid.Empty
                || opened.TargetRevision <= 0
                || opened.CapabilitySummaryRevision <= 0
                || !IsName(opened.DisplayName)
                || opened.Capabilities == null
                || opened.Capabilities.Length > MaximumWorldActorCapabilities)
            {
                return false;
            }

            for (var index = 0; index < opened.Capabilities.Length; index++)
            {
                if (!IsValidWorldActorCapability(opened.Capabilities[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidWorldActorCapability(RealtimeWorldActorCapability capability)
        {
            return capability != null
                && IsCapabilityId(capability.Id)
                && Enum.IsDefined(typeof(RealtimeWorldActorCapabilityKind), capability.Kind)
                && !string.IsNullOrWhiteSpace(capability.DisplayName)
                && capability.DisplayName.Length <= MaximumWorldActorCapabilityNameLength
                && capability.Revision > 0;
        }

        private static bool IsValidWorldInteractionResult(RealtimeWorldInteractionResult result)
        {
            return result != null
                && result.OperationId != Guid.Empty
                && Enum.IsDefined(typeof(RealtimeWorldInteractionOperationKind), result.OperationKind)
                && result.TargetRevision >= 0
                && (result.OperationKind == RealtimeWorldInteractionOperationKind.Open
                    ? result.InteractionSessionId == Guid.Empty && !result.Succeeded
                    : result.InteractionSessionId != Guid.Empty)
                && (!result.Succeeded || result.TargetRevision > 0)
                && (result.Succeeded
                    ? result.Error == null
                    : result.Error != null
                        && !string.IsNullOrWhiteSpace(result.Error.Code)
                        && result.Error.Code.Length <= MaximumErrorCodeLength
                        && !string.IsNullOrWhiteSpace(result.Error.Message)
                        && result.Error.Message.Length <= MaximumErrorMessageLength);
        }

        private static bool IsValidWorldInteractionClosed(RealtimeWorldInteractionClosed closed)
        {
            return closed != null
                && closed.InteractionSessionId != Guid.Empty
                && Enum.IsDefined(typeof(RealtimeWorldInteractionTargetKind), closed.TargetKind)
                && closed.TargetEntityId != 0
                && closed.TargetRuntimeId != Guid.Empty
                && !string.IsNullOrWhiteSpace(closed.Code)
                && closed.Code.Length <= MaximumErrorCodeLength
                && !string.IsNullOrWhiteSpace(closed.Message)
                && closed.Message.Length <= MaximumErrorMessageLength;
        }

        private static bool IsIdentifier(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumIdentifierLength;
        }

        private static bool IsName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumNameLength;
        }

        private static bool IsCapabilityId(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length <= MaximumWorldActorCapabilityIdLength;
        }
    }
}
