using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    public sealed class WorldInteractionClientState
    {
        private TextCatalog texts = TextCatalog.Empty(TextTableFormat.SourceLocale);

        public event Action Changed;

        // The texts for the codes that the worker sends with a result or a closure.
        public void UseTexts(TextCatalog catalog)
        {
            texts = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public RealtimeWorldInteractionOpened ActiveInteraction { get; private set; }

        // The actor of the open interaction, which names it and its capabilities, or null.
        public WorldActorClientEntry ActiveTarget { get; private set; }

        // The name of the actor whose interaction is open, or empty.
        public string ActiveDisplayName
        {
            get { return ActiveTarget != null ? ActiveTarget.DisplayName : string.Empty; }
        }

        public Guid PendingOperationId { get; private set; }

        // The name of a capability of the open interaction, or its id without a target.
        public string CapabilityName(RealtimeWorldActorCapability capability)
        {
            if (capability == null)
            {
                return string.Empty;
            }

            return ActiveTarget != null ? ActiveTarget.CapabilityName(capability.Id) : capability.Id;
        }

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
            WorldActorClientEntry target,
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
            ActiveTarget = target;
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
            // A committed operation may carry a notice, and a failed one carries its error;
            // the arguments belong to whichever text is shown.
            LastErrorCode = result.Succeeded ? string.Empty : result.Error.Code;
            var code = result.Succeeded ? result.NoticeCode : result.Error.Code;
            LastMessage = string.IsNullOrEmpty(code)
                ? string.Empty
                : ClientMessages.Text(
                    texts,
                    code,
                    result.Succeeded ? null : result.Error.Message,
                    RealtimeMessageArguments.ToText(result.Arguments));
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
            ActiveTarget = null;
            LastErrorCode = closed.Code;
            LastMessage = ClientMessages.Text(texts, closed.Code, closed.Message);
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
            ActiveTarget = null;
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
        // Dialogue, insurance, and quests: the panel presents the choices.
        OpenOnly,

        // Vendor, bank, Recovery Storage, and the other services run at once.
        Execute,

        StartGathering
    }

    [DisallowMultipleComponent]
    public sealed class WorldInteractionClientController : MonoBehaviour
    {
        private IRealtimeWorldInteractionChannel interactionChannel;
        private IRealtimeSession realtimeSession;
        private WorldActorClientController actorController;
        private CorpseClientController corpseController;
        private InventoryClientController inventoryController;
        private TextCatalog messageTexts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        private bool initialized;
        private Guid pendingCapabilityOperationId;
        private RealtimeWorldActorCapability pendingCapability;
        private RealtimeCapabilityActionKind pendingCapabilityAction;
        private Guid pendingOpenOperationId;
        private string pendingOpenCapabilityId = string.Empty;
        // Vendor trades in the inventory's queue, whose results this controller hands back.
        private readonly List<InventoryPredictedOperation> queuedOperations = new List<InventoryPredictedOperation>();

        // Raised when the server confirms an opened Character Bank, Recovery Storage, or
        // Vendor capability. The inventory presents it while the interaction stays open.
        public event Action<RealtimeWorldActorCapability> InventoryCapabilityOpened;

        // Raised when the player chooses the quests capability of the open interaction, so the
        // quest dialog opens for the NPC. Its actions need no other call to open.
        public event Action<RealtimeWorldActorCapability> QuestCapabilityOpened;

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
            IRealtimeWorldInteractionChannel interactions,
            IRealtimeSession session,
            WorldActorClientController worldActors,
            CorpseClientController corpses,
            InventoryClientController inventory,
            TextCatalog texts)
        {
            if (interactions == null
                || session == null
                || worldActors == null
                || corpses == null
                || inventory == null
                || texts == null)
            {
                throw new ArgumentNullException(
                    "World interaction requires realtime, actor, corpse, and inventory controllers and the texts.");
            }

            messageTexts = texts;
            State.UseTexts(texts);
            Gathering.UseTexts(texts);

            if (initialized)
            {
                Unsubscribe();
            }

            interactionChannel = interactions;
            realtimeSession = session;
            actorController = worldActors;
            corpseController = corpses;
            inventoryController = inventory;
            interactionChannel.WorldInteractionOpened += OnOpened;
            interactionChannel.WorldInteractionCompleted += OnCompleted;
            interactionChannel.WorldInteractionClosed += OnClosed;
            interactionChannel.GatheringStateReceived += OnGatheringState;
            realtimeSession.SessionEnded += OnSessionEnded;
            inventoryController.State.PredictionsDiscarded += OnPredictionsDiscarded;
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

            if (!Guid.TryParse(
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
            if (!interactionChannel.TrySendWorldInteraction(intent))
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
                case RealtimeWorldActorCapabilityKind.Quests:
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
                    messageTexts.Format(
                        "ui/world/capability-not-offered",
                        MessageArgument.Text("npc", State.ActiveDisplayName)));
                return;
            }

            string error;
            switch (ActivationFor(capability.Kind))
            {
                case WorldCapabilityActivation.OpenOnly:
                    if (capability.Kind == RealtimeWorldActorCapabilityKind.Quests)
                    {
                        QuestCapabilityOpened?.Invoke(capability);
                    }

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
                            Text("ui/world/gathering-tool-required"));
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
                error = Text("ui/world/no-idle-interaction");
                return false;
            }

            if (!Guid.TryParse(
                    ClientSession.ActiveSimulationSession?.simulationSessionId,
                    out var simulationSessionId))
            {
                error = Text("ui/world/session-unavailable");
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
            if (!interactionChannel.TrySendWorldInteraction(intent))
            {
                error = Text("ui/world/close-unsent");
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
                error = Text("ui/world/wait-for-interaction");
                return false;
            }

            if (corpseController?.State.ActiveView != null)
            {
                return corpseController.TryClose(out error);
            }

            if (corpseController?.State.PendingOperationId != Guid.Empty)
            {
                error = Text("ui/world/wait-for-corpse");
                return false;
            }

            error = Text("ui/world/nothing-open");
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

        // Buys a quantity of a vendor offer for the total the player was shown. The purchase
        // shows at once and is sent in order with every other item operation.
        public bool TryPurchase(
            string capabilityId,
            string definitionId,
            int quantity,
            long expectedPrice,
            out string error)
        {
            return TryQueueVendorOperation(
                capabilityId,
                VendorPrediction.Purchase(
                    definitionId,
                    quantity,
                    expectedPrice,
                    (InventoryClientState inventory, out Guid operationId, out string sendError) =>
                        SendCapabilityAction(
                            capabilityId,
                            RealtimeCapabilityActionKind.VendorPurchase,
                            inventory.KnownItemStateRevision,
                            Guid.Empty,
                            0,
                            definitionId,
                            quantity,
                            expectedPrice,
                            out operationId,
                            out sendError)),
                out error);
        }

        // Sells a quantity of a carried item for the total the player was shown.
        public bool TrySell(
            string capabilityId,
            Guid itemInstanceId,
            int quantity,
            long expectedPrice,
            out string error)
        {
            return TryQueueVendorOperation(
                capabilityId,
                VendorPrediction.Sale(
                    itemInstanceId,
                    quantity,
                    expectedPrice,
                    (InventoryClientState inventory, out Guid operationId, out string sendError) =>
                    {
                        if (!inventory.TryFindConfirmedItem(itemInstanceId, out var item, out _))
                        {
                            operationId = Guid.Empty;
                            sendError = Text("ui/world/item-changed-before-sale");
                            return InventoryOperationSendResult.Failed;
                        }

                        return SendCapabilityAction(
                            capabilityId,
                            RealtimeCapabilityActionKind.VendorSale,
                            inventory.KnownItemStateRevision,
                            itemInstanceId,
                            item.Revision,
                            string.Empty,
                            quantity,
                            expectedPrice,
                            out operationId,
                            out sendError);
                    }),
                out error);
        }

        private bool TryQueueVendorOperation(
            string capabilityId,
            InventoryPredictedOperation operation,
            out string error)
        {
            if (State.ActiveInteraction == null
                || !Array.Exists(
                    State.ActiveInteraction.Capabilities,
                    value => string.Equals(value.Id, capabilityId, StringComparison.Ordinal) && value.IsAvailable))
            {
                error = Text("ui/world/capability-unavailable");
                return false;
            }

            if (!inventoryController.TryQueueOperation(operation, out error))
            {
                return false;
            }

            queuedOperations.Add(operation);
            return true;
        }

        // Sends a queued vendor trade once the interaction is idle; it waits while another
        // world operation is in flight and fails once the interaction closed.
        private InventoryOperationSendResult SendCapabilityAction(
            string capabilityId,
            RealtimeCapabilityActionKind action,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            string offerDefinitionId,
            int quantity,
            long expectedPrice,
            out Guid operationId,
            out string error)
        {
            operationId = Guid.Empty;
            if (State.ActiveInteraction != null && State.PendingOperationId != Guid.Empty)
            {
                error = string.Empty;
                return InventoryOperationSendResult.Wait;
            }

            if (!TryExecuteCapability(
                    capabilityId,
                    action,
                    expectedCharacterRevision,
                    itemInstanceId,
                    expectedItemRevision,
                    Guid.Empty,
                    -1,
                    out error,
                    offerDefinitionId,
                    quantity,
                    expectedPrice))
            {
                return InventoryOperationSendResult.Failed;
            }

            operationId = pendingCapabilityOperationId;
            return InventoryOperationSendResult.Sent;
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
            long expectedPrice = 0,
            string questId = "",
            string questObjectiveId = "",
            string rewardChoiceDefinitionId = "")
        {
            error = string.Empty;
            var active = State.ActiveInteraction;
            if (active == null || State.PendingOperationId != Guid.Empty)
            {
                error = Text("ui/world/idle-interaction-required");
                return false;
            }

            var capability = Array.Find(
                active.Capabilities,
                value => string.Equals(value.Id, capabilityId, StringComparison.Ordinal));
            if (capability == null || !capability.IsAvailable)
            {
                error = Text("ui/world/capability-unavailable");
                return false;
            }

            if (!Guid.TryParse(
                    ClientSession.ActiveSimulationSession?.simulationSessionId,
                    out var simulationSessionId))
            {
                error = Text("ui/world/session-unavailable");
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
                expectedPrice,
                questId ?? string.Empty,
                questObjectiveId ?? string.Empty,
                rewardChoiceDefinitionId ?? string.Empty);
            if (!interactionChannel.TrySendWorldInteraction(intent))
            {
                error = Text("ui/world/capability-unsent");
                return false;
            }

            State.Begin(operationId);
            pendingCapabilityOperationId = operationId;
            pendingCapability = capability;
            pendingCapabilityAction = action;
            return true;
        }

        // A quest action at the NPC of the open interaction: accept, grant start items again,
        // talk, hand over, or turn in with a reward choice. The worker checks it against the
        // quest log and the NPC.
        public bool TryExecuteQuestAction(
            RealtimeCapabilityActionKind action,
            string questId,
            string objectiveId,
            string rewardChoiceDefinitionId,
            out string error)
        {
            var capability = State.ActiveInteraction == null
                ? null
                : Array.Find(
                    State.ActiveInteraction.Capabilities,
                    value => value.Kind == RealtimeWorldActorCapabilityKind.Quests);
            if (capability == null)
            {
                error = Text("ui/world/no-quests");
                return false;
            }

            return TryExecuteCapability(
                capability.Id,
                action,
                0,
                Guid.Empty,
                0,
                Guid.Empty,
                -1,
                out error,
                questId: questId,
                questObjectiveId: objectiveId,
                rewardChoiceDefinitionId: rewardChoiceDefinitionId);
        }

        // The actor of the open interaction, such as the NPC whose quests the dialog shows.
        public bool TryGetActiveActor(out WorldActorClientEntry actor)
        {
            actor = null;
            var active = State.ActiveInteraction;
            return active != null
                && actorController != null
                && actorController.State.TryGet(active.TargetEntityId, out actor)
                && actor != null
                && actor.RuntimeActorId == active.TargetRuntimeId;
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
            if (realtimeSession == null || !realtimeSession.IsJoined)
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

        // The controller's own error, as the text the player reads.
        private string Text(string key)
        {
            return messageTexts.Get(key);
        }

        private void OnOpened(RealtimeWorldInteractionOpened opened)
        {
            if (!actorController.State.TryGet(opened.TargetEntityId, out var actor)
                || actor.RuntimeActorId != opened.TargetRuntimeId
                || opened.TargetRevision < actor.InteractionRevision)
            {
                realtimeSession.DisconnectForClientFailure(
                    "world_interaction_state_conflict",
                    "World interaction open conflicts with actor presence.");
                return;
            }

            var capabilityId = opened.OperationId == pendingOpenOperationId
                ? pendingOpenCapabilityId
                : string.Empty;
            ClearPendingOpen();
            if (!State.TryOpen(opened, actor, out var error))
            {
                realtimeSession.DisconnectForClientFailure(
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
                realtimeSession.DisconnectForClientFailure(
                    "world_interaction_operation_conflict",
                    error);
                return;
            }

            var queued = queuedOperations.FirstOrDefault(operation =>
                operation.IsSent && operation.OperationId == result.OperationId);
            if (queued != null)
            {
                queuedOperations.Remove(queued);
                inventoryController.CompleteExternalOperation(
                    result.OperationId,
                    result.Succeeded,
                    result.Succeeded
                        ? null
                        : InventoryClientError.FromServer(result.Error, messageTexts),
                    false,
                    result.ItemStateRevision > 0 ? result.ItemStateRevision : inventoryController.State.KnownItemStateRevision);
            }
            else if (result.Succeeded && result.ItemStateRevision > 0)
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

        private void OnPredictionsDiscarded(IReadOnlyList<InventoryPredictedOperation> operations)
        {
            queuedOperations.RemoveAll(operations.Contains);
        }

        private void OnSessionEnded(RealtimeClientError error)
        {
            queuedOperations.Clear();
            State.Clear();
            Gathering.Clear();
            ClearPendingOpen();
        }

        private void Unsubscribe()
        {
            if (!initialized || interactionChannel == null)
            {
                return;
            }

            interactionChannel.WorldInteractionOpened -= OnOpened;
            interactionChannel.WorldInteractionCompleted -= OnCompleted;
            interactionChannel.WorldInteractionClosed -= OnClosed;
            interactionChannel.GatheringStateReceived -= OnGatheringState;
            realtimeSession.SessionEnded -= OnSessionEnded;
            inventoryController.State.PredictionsDiscarded -= OnPredictionsDiscarded;
            initialized = false;
        }
    }
}
