using System;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    public sealed class WorldInteractionClientState
    {
        public event Action Changed;

        public RealtimeWorldInteractionOpened ActiveInteraction { get; private set; }

        public Guid PendingOperationId { get; private set; }

        public string LastErrorCode { get; private set; } = string.Empty;

        public string LastMessage { get; private set; } = string.Empty;

        public void Begin(Guid operationId)
        {
            PendingOperationId = operationId;
            LastErrorCode = string.Empty;
            LastMessage = string.Empty;
            Changed?.Invoke();
        }

        public bool TryOpen(
            RealtimeWorldInteractionOpened opened,
            out string error)
        {
            error = string.Empty;
            if (opened == null || opened.OperationId != PendingOperationId)
            {
                error = "World interaction open does not match the pending operation.";
                return false;
            }

            if (ActiveInteraction != null
                && ActiveInteraction.InteractionSessionId != opened.InteractionSessionId)
            {
                error = "World interaction session changed without a closure.";
                return false;
            }

            ActiveInteraction = opened;
            PendingOperationId = Guid.Empty;
            LastErrorCode = string.Empty;
            LastMessage = string.Empty;
            Changed?.Invoke();
            return true;
        }

        public bool TryComplete(
            RealtimeWorldInteractionResult result,
            out string error)
        {
            error = string.Empty;
            if (result == null || result.OperationId != PendingOperationId)
            {
                error = "World interaction result does not match the pending operation.";
                return false;
            }

            if (ActiveInteraction != null
                && result.InteractionSessionId != Guid.Empty
                && ActiveInteraction.InteractionSessionId != result.InteractionSessionId)
            {
                error = "World interaction result belongs to another interaction session.";
                return false;
            }

            PendingOperationId = Guid.Empty;
            LastErrorCode = result.Succeeded ? string.Empty : result.Error.Code;
            LastMessage = result.Succeeded ? result.Message : result.Error.Message;
            Changed?.Invoke();
            return true;
        }

        public void Close(RealtimeWorldInteractionClosed closed)
        {
            if (closed == null
                || ActiveInteraction == null
                || ActiveInteraction.InteractionSessionId
                    != closed.InteractionSessionId)
            {
                return;
            }

            ActiveInteraction = null;
            LastErrorCode = closed.Code;
            LastMessage = closed.Message;
            Changed?.Invoke();
        }

        public void ClearMessage()
        {
            if (string.IsNullOrEmpty(LastErrorCode)
                && string.IsNullOrEmpty(LastMessage))
            {
                return;
            }

            LastErrorCode = string.Empty;
            LastMessage = string.Empty;
            Changed?.Invoke();
        }

        public void SetLocalError(string code, string message)
        {
            LastErrorCode = code ?? string.Empty;
            LastMessage = message ?? string.Empty;
            Changed?.Invoke();
        }

        public void Clear()
        {
            ActiveInteraction = null;
            PendingOperationId = Guid.Empty;
            LastErrorCode = string.Empty;
            LastMessage = string.Empty;
            Changed?.Invoke();
        }
    }

    // What choosing a capability in the world menu does once its interaction is open. It
    // mirrors the interaction panel: a capability that needs a choice there only opens.
    public enum WorldCapabilityActivation
    {
        // Dialogue, insurance, and quest offers: the panel presents the choices.
        OpenOnly,

        // Vendor, bank, Recovery Storage, and the other services run at once.
        Execute,

        StartGathering
    }

    [DisallowMultipleComponent]
    public sealed class WorldInteractionClientController : MonoBehaviour
    {
        private RealtimeSimulationClient simulationClient;
        private WorldActorClientController actorController;
        private CorpseClientController corpseController;
        private InventoryClientController inventoryController;
        private bool initialized;
        private Guid pendingCapabilityOperationId;
        private RealtimeWorldActorCapability pendingCapability;
        private RealtimeCapabilityActionKind pendingCapabilityAction;
        private Guid pendingOpenOperationId;
        private string pendingOpenCapabilityId = string.Empty;

        // Raised when the server confirms an opened Character Bank, Recovery Storage, or
        // Vendor capability. The inventory presents it while the interaction stays open.
        public event Action<RealtimeWorldActorCapability> InventoryCapabilityOpened;

        public WorldInteractionClientState State { get; } =
            new WorldInteractionClientState();

        public GatheringClientState Gathering { get; } = new GatheringClientState();

        public bool HasActiveLease
        {
            get
            {
                return State.ActiveInteraction != null
                    || State.PendingOperationId != Guid.Empty
                    || corpseController?.State.ActiveView != null
                    || corpseController?.State.PendingOperationId != Guid.Empty;
            }
        }

        public void Initialize(
            RealtimeSimulationClient realtimeClient,
            WorldActorClientController worldActors,
            CorpseClientController corpses,
            InventoryClientController inventory)
        {
            if (realtimeClient == null
                || worldActors == null
                || corpses == null
                || inventory == null)
            {
                throw new ArgumentNullException(
                    "World interaction requires realtime, actor, corpse, and inventory controllers.");
            }

            if (initialized)
            {
                Unsubscribe();
            }

            simulationClient = realtimeClient;
            actorController = worldActors;
            corpseController = corpses;
            inventoryController = inventory;
            simulationClient.WorldInteractionOpened += OnOpened;
            simulationClient.WorldInteractionCompleted += OnCompleted;
            simulationClient.WorldInteractionClosed += OnClosed;
            simulationClient.GatheringStateReceived += OnGatheringState;
            simulationClient.SessionEnded += OnSessionEnded;
            initialized = true;
            State.Clear();
            Gathering.Clear();
            ClearPendingOpen();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        // Opens the actor's interaction and, once it is open, activates the capability, when
        // one is named.
        public bool TryOpenActor(WorldActorClientEntry actor, string capabilityId, out string error)
        {
            error = string.Empty;
            if (actor == null || !actor.IsActive)
            {
                error = "An active world actor target is required.";
                return false;
            }

            if (!CanBeginWorldOperation(out error))
            {
                return false;
            }

            if (simulationClient.MovementSession == null
                || !Guid.TryParse(
                    ClientSession.ActiveSimulationSession?.simulationSessionId,
                    out var simulationSessionId))
            {
                error = "The active simulation session is unavailable.";
                return false;
            }

            var operationId = Guid.NewGuid();
            var intent = RealtimeWorldInteractionIntent.CreateOpen(
                operationId,
                simulationSessionId,
                RealtimeWorldInteractionTargetKind.WorldActor,
                actor.EntityId,
                actor.RuntimeActorId,
                actor.InteractionRevision);
            if (!simulationClient.TrySendWorldInteraction(intent))
            {
                error = "The world interaction open request could not be sent.";
                return false;
            }

            State.Begin(operationId);
            pendingOpenOperationId = operationId;
            pendingOpenCapabilityId = capabilityId ?? string.Empty;
            return true;
        }

        public static WorldCapabilityActivation ActivationFor(RealtimeWorldActorCapabilityKind kind)
        {
            switch (kind)
            {
                case RealtimeWorldActorCapabilityKind.Gathering:
                    return WorldCapabilityActivation.StartGathering;
                case RealtimeWorldActorCapabilityKind.Dialogue:
                case RealtimeWorldActorCapabilityKind.Insurance:
                case RealtimeWorldActorCapabilityKind.QuestOffer:
                    return WorldCapabilityActivation.OpenOnly;
                default:
                    return WorldCapabilityActivation.Execute;
            }
        }

        // Starts a capability of the open interaction, as its panel button would. Failures
        // show in the interaction panel.
        public void ActivateCapability(string capabilityId)
        {
            var active = State.ActiveInteraction;
            if (active == null || string.IsNullOrEmpty(capabilityId))
            {
                return;
            }

            var capability = Array.Find(
                active.Capabilities,
                value => string.Equals(value.Id, capabilityId, StringComparison.Ordinal));
            if (capability == null)
            {
                State.SetLocalError(
                    "world_interaction_capability_missing",
                    active.DisplayName + " does not offer that right now.");
                return;
            }

            string error;
            switch (ActivationFor(capability.Kind))
            {
                case WorldCapabilityActivation.OpenOnly:
                    return;
                case WorldCapabilityActivation.StartGathering:
                    if (Gathering.InteractionSessionId == active.InteractionSessionId
                        && Gathering.Status == GatheringClientStatus.Gathering)
                    {
                        return;
                    }

                    if (!HasEquippedGatheringTool(inventoryController.State))
                    {
                        State.SetLocalError(
                            "gathering_tool_required",
                            "Equip a gathering tool, such as a pickaxe, to gather here.");
                        return;
                    }

                    if (!TryStartGathering(capability.Id, out error))
                    {
                        State.SetLocalError("world_interaction_client_rejected", error);
                    }

                    return;
                default:
                    if (!TryExecuteCapability(capability.Id, out error))
                    {
                        State.SetLocalError("world_interaction_client_rejected", error);
                    }

                    return;
            }
        }

        public bool TryOpenCorpse(Guid corpseId, out string error)
        {
            error = string.Empty;
            if (State.ActiveInteraction != null
                || State.PendingOperationId != Guid.Empty)
            {
                error = "Close the active world interaction before opening a corpse.";
                return false;
            }

            return corpseController.TryOpen(corpseId, out error);
        }

        public bool TryClose(out string error)
        {
            error = string.Empty;
            var active = State.ActiveInteraction;
            if (active == null || State.PendingOperationId != Guid.Empty)
            {
                error = "No idle world interaction is open.";
                return false;
            }

            if (!Guid.TryParse(
                    ClientSession.ActiveSimulationSession?.simulationSessionId,
                    out var simulationSessionId))
            {
                error = "The active simulation session is unavailable.";
                return false;
            }

            var operationId = Guid.NewGuid();
            var intent = RealtimeWorldInteractionIntent.CreateClose(
                operationId,
                simulationSessionId,
                active.InteractionSessionId,
                active.TargetKind,
                active.TargetEntityId,
                active.TargetRuntimeId,
                active.TargetRevision);
            if (!simulationClient.TrySendWorldInteraction(intent))
            {
                error = "The world interaction close request could not be sent.";
                return false;
            }

            State.Begin(operationId);
            return true;
        }

        public bool TryCloseActive(out string error)
        {
            if (State.ActiveInteraction != null)
            {
                return TryClose(out error);
            }

            if (State.PendingOperationId != Guid.Empty)
            {
                error = "Wait for the pending world interaction operation to finish.";
                return false;
            }

            if (corpseController?.State.ActiveView != null)
            {
                return corpseController.TryClose(out error);
            }

            if (corpseController?.State.PendingOperationId != Guid.Empty)
            {
                error = "Wait for the pending corpse interaction operation to finish.";
                return false;
            }

            error = "No world interaction or corpse view is open.";
            return false;
        }

        public bool TryExecuteCapability(string capabilityId, out string error)
        {
            return TryExecuteCapability(
                capabilityId,
                RealtimeCapabilityActionKind.None,
                0,
                Guid.Empty,
                0,
                Guid.Empty,
                -1,
                out error);
        }

        // Buys a quantity of a vendor offer for the total the player was shown.
        public bool TryPurchase(
            string capabilityId,
            string definitionId,
            int quantity,
            long expectedPrice,
            long expectedCharacterRevision,
            out string error)
        {
            return TryExecuteCapability(
                capabilityId,
                RealtimeCapabilityActionKind.VendorPurchase,
                expectedCharacterRevision,
                Guid.Empty,
                0,
                Guid.Empty,
                -1,
                out error,
                definitionId,
                quantity,
                expectedPrice);
        }

        // Sells a quantity of a carried item for the total the player was shown.
        public bool TrySell(
            string capabilityId,
            Guid itemInstanceId,
            long expectedItemRevision,
            int quantity,
            long expectedPrice,
            long expectedCharacterRevision,
            out string error)
        {
            return TryExecuteCapability(
                capabilityId,
                RealtimeCapabilityActionKind.VendorSale,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                Guid.Empty,
                -1,
                out error,
                string.Empty,
                quantity,
                expectedPrice);
        }

        public bool TryStartGathering(string capabilityId, out string error)
        {
            return TryExecuteCapability(
                capabilityId,
                RealtimeCapabilityActionKind.StartGathering,
                0,
                Guid.Empty,
                0,
                Guid.Empty,
                -1,
                out error);
        }

        public bool TryExecuteCapability(
            string capabilityId,
            RealtimeCapabilityActionKind action,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            int destinationSlotIndex,
            out string error,
            string offerDefinitionId = "",
            int quantity = 0,
            long expectedPrice = 0)
        {
            error = string.Empty;
            var active = State.ActiveInteraction;
            if (active == null || State.PendingOperationId != Guid.Empty)
            {
                error = "An idle world interaction is required.";
                return false;
            }

            var capability = Array.Find(
                active.Capabilities,
                value => string.Equals(value.Id, capabilityId, StringComparison.Ordinal));
            if (capability == null || !capability.IsAvailable)
            {
                error = "The selected capability is not currently available.";
                return false;
            }

            if (!Guid.TryParse(
                    ClientSession.ActiveSimulationSession?.simulationSessionId,
                    out var simulationSessionId))
            {
                error = "The active simulation session is unavailable.";
                return false;
            }

            var operationId = Guid.NewGuid();
            var intent = RealtimeWorldInteractionIntent.CreateCapabilityAction(
                operationId,
                simulationSessionId,
                active.InteractionSessionId,
                active.TargetKind,
                active.TargetEntityId,
                active.TargetRuntimeId,
                active.TargetRevision,
                capabilityId,
                capability.Kind,
                capability.Revision,
                action,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                destinationContainerId,
                destinationSlotIndex,
                offerDefinitionId ?? string.Empty,
                quantity,
                expectedPrice);
            if (!simulationClient.TrySendWorldInteraction(intent))
            {
                error = "The capability action request could not be sent.";
                return false;
            }

            State.Begin(operationId);
            pendingCapabilityOperationId = operationId;
            pendingCapability = capability;
            pendingCapabilityAction = action;
            return true;
        }

        // Unknown until the inventory has loaded, so only a loaded inventory without a
        // tool blocks the start.
        public static bool HasEquippedGatheringTool(InventoryClientState inventory)
        {
            if (inventory?.FullSnapshot == null || inventory.Catalog == null)
            {
                return true;
            }

            foreach (var slot in inventory.FullSnapshot.Equipment)
            {
                if (slot.Item != null
                    && string.Equals(slot.SlotId, ItemEquipmentSlotIds.Tool, StringComparison.Ordinal)
                    && inventory.Catalog.TryGetDefinition(slot.Item.DefinitionId, out var definition)
                    && definition.ToolKinds != null
                    && definition.ToolKinds.Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private bool CanBeginWorldOperation(out string error)
        {
            error = string.Empty;
            if (simulationClient == null || !simulationClient.IsJoined)
            {
                error = "World interaction requires an active simulation connection.";
                return false;
            }

            if (State.ActiveInteraction != null
                || State.PendingOperationId != Guid.Empty
                || corpseController.State.ActiveView != null
                || corpseController.State.PendingOperationId != Guid.Empty)
            {
                error = "Close the active world interaction or corpse before opening another target.";
                return false;
            }

            return true;
        }

        private void OnOpened(RealtimeWorldInteractionOpened opened)
        {
            if (!actorController.State.TryGet(opened.TargetEntityId, out var actor)
                || actor.RuntimeActorId != opened.TargetRuntimeId
                || opened.TargetRevision < actor.InteractionRevision)
            {
                simulationClient.DisconnectForClientFailure(
                    "world_interaction_state_conflict",
                    "World interaction open conflicts with actor presence.");
                return;
            }

            var capabilityId = opened.OperationId == pendingOpenOperationId
                ? pendingOpenCapabilityId
                : string.Empty;
            ClearPendingOpen();
            if (!State.TryOpen(opened, out var error))
            {
                simulationClient.DisconnectForClientFailure(
                    "world_interaction_state_conflict",
                    error);
                return;
            }

            // The tool check before gathering is advisory: GameService checks the equipped
            // tool on every yield.
            ActivateCapability(capabilityId);
        }

        private void OnCompleted(RealtimeWorldInteractionResult result)
        {
            // A rejected open never opens, so its follow-up capability is dropped.
            if (result != null && result.OperationId == pendingOpenOperationId)
            {
                ClearPendingOpen();
            }

            if (!State.TryComplete(result, out var error))
            {
                simulationClient.DisconnectForClientFailure(
                    "world_interaction_operation_conflict",
                    error);
                return;
            }

            if (result.Succeeded && result.ItemStateRevision > 0)
            {
                inventoryController.EnsureFullState(
                    result.ItemStateRevision);
            }

            if (result.OperationId == pendingCapabilityOperationId)
            {
                var capability = pendingCapability;
                var action = pendingCapabilityAction;
                pendingCapabilityOperationId = Guid.Empty;
                pendingCapability = null;
                if (result.Succeeded
                    && action == RealtimeCapabilityActionKind.None
                    && capability.Kind is RealtimeWorldActorCapabilityKind.Bank
                        or RealtimeWorldActorCapabilityKind.RecoveryStorage
                        or RealtimeWorldActorCapabilityKind.Vendor)
                {
                    InventoryCapabilityOpened?.Invoke(capability);
                }
            }
        }

        private void ClearPendingOpen()
        {
            pendingOpenOperationId = Guid.Empty;
            pendingOpenCapabilityId = string.Empty;
        }

        private void OnClosed(RealtimeWorldInteractionClosed closed)
        {
            State.Close(closed);
        }

        // A yield always refreshes the inventory to its committed revision. A state for
        // another gathering session is ignored, because gathering never changes interaction
        // or item state by itself.
        private void OnGatheringState(RealtimeGatheringState gathering)
        {
            if (gathering.Kind == RealtimeGatheringStateKind.Yielded
                && gathering.ItemStateRevision > 0)
            {
                inventoryController.EnsureFullState(gathering.ItemStateRevision);
            }

            var activeSessionId = State.ActiveInteraction?.InteractionSessionId ?? Guid.Empty;
            if (!Gathering.TryApply(gathering, activeSessionId, Time.unscaledTime, out var error))
            {
                ClientLog.Warning(ClientLogCategory.Client, "Ignored gathering state: " + error);
            }
        }

        private void OnSessionEnded(RealtimeClientError error)
        {
            State.Clear();
            Gathering.Clear();
            ClearPendingOpen();
        }

        private void Unsubscribe()
        {
            if (!initialized || simulationClient == null)
            {
                return;
            }

            simulationClient.WorldInteractionOpened -= OnOpened;
            simulationClient.WorldInteractionCompleted -= OnCompleted;
            simulationClient.WorldInteractionClosed -= OnClosed;
            simulationClient.GatheringStateReceived -= OnGatheringState;
            simulationClient.SessionEnded -= OnSessionEnded;
            initialized = false;
        }
    }
}
