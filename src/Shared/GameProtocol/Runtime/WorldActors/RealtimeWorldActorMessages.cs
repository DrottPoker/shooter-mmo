using System;

namespace Mmorpg.GameProtocol
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

    public enum RealtimeNpcLifecycleActionKind : byte
    {
        None = 0,
        ApplyInsurance = 1,
        RemoveInsurance = 2,
        AcceptQuest = 3,
        AbandonQuest = 4
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
            long expectedCapabilityRevision,
            RealtimeNpcLifecycleActionKind lifecycleAction,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            int destinationSlotIndex)
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
            LifecycleAction = lifecycleAction;
            ExpectedCharacterRevision = expectedCharacterRevision;
            ItemInstanceId = itemInstanceId;
            ExpectedItemRevision = expectedItemRevision;
            DestinationContainerId = destinationContainerId;
            DestinationSlotIndex = destinationSlotIndex;
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
        public RealtimeNpcLifecycleActionKind LifecycleAction { get; }
        public long ExpectedCharacterRevision { get; }
        public Guid ItemInstanceId { get; }
        public long ExpectedItemRevision { get; }
        public Guid DestinationContainerId { get; }
        public int DestinationSlotIndex { get; }

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
                0,
                default,
                0,
                Guid.Empty,
                0,
                Guid.Empty,
                -1);
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
                0,
                default,
                0,
                Guid.Empty,
                0,
                Guid.Empty,
                -1);
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
            long expectedCapabilityRevision,
            RealtimeNpcLifecycleActionKind lifecycleAction = RealtimeNpcLifecycleActionKind.None,
            long expectedCharacterRevision = 0,
            Guid itemInstanceId = default,
            long expectedItemRevision = 0,
            Guid destinationContainerId = default,
            int destinationSlotIndex = -1)
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
                expectedCapabilityRevision,
                lifecycleAction,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                destinationContainerId,
                destinationSlotIndex);
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
            RealtimeError error,
            string message = "",
            long itemStateRevision = 0)
        {
            OperationId = operationId;
            InteractionSessionId = interactionSessionId;
            OperationKind = operationKind;
            Succeeded = succeeded;
            TargetRevision = targetRevision;
            Error = error;
            Message = message ?? string.Empty;
            ItemStateRevision = itemStateRevision;
        }

        public Guid OperationId { get; }
        public Guid InteractionSessionId { get; }
        public RealtimeWorldInteractionOperationKind OperationKind { get; }
        public bool Succeeded { get; }
        public long TargetRevision { get; }
        public RealtimeError Error { get; }
        public string Message { get; }
        public long ItemStateRevision { get; }
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
}
