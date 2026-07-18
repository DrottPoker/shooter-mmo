using System;
using ShooterMmo.GameProtocol;
using ShooterMmo.Items;
using ShooterMmo.Networking;
using UnityEngine;

namespace ShooterMmo.WorldActors
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
            LastMessage = result.Succeeded ? string.Empty : result.Error.Message;
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

    [DisallowMultipleComponent]
    public sealed class WorldInteractionClientController : MonoBehaviour
    {
        private RealtimeSimulationClient simulationClient;
        private WorldActorClientController actorController;
        private CorpseClientController corpseController;
        private bool initialized;

        public WorldInteractionClientState State { get; } =
            new WorldInteractionClientState();

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
            CorpseClientController corpses)
        {
            if (realtimeClient == null
                || worldActors == null
                || corpses == null)
            {
                throw new ArgumentNullException(
                    "World interaction requires realtime, actor, and corpse controllers.");
            }

            if (initialized)
            {
                Unsubscribe();
            }

            simulationClient = realtimeClient;
            actorController = worldActors;
            corpseController = corpses;
            simulationClient.WorldInteractionOpened += OnOpened;
            simulationClient.WorldInteractionCompleted += OnCompleted;
            simulationClient.WorldInteractionClosed += OnClosed;
            simulationClient.UnexpectedlyDisconnected += OnDisconnected;
            initialized = true;
            State.Clear();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        public bool TryOpenActor(WorldActorClientEntry actor, out string error)
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
                    ShooterMmoClientSession.ActiveSimulationSession?.simulationSessionId,
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
            return true;
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
                    ShooterMmoClientSession.ActiveSimulationSession?.simulationSessionId,
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
                    ShooterMmoClientSession.ActiveSimulationSession?.simulationSessionId,
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
                capability.Revision);
            if (!simulationClient.TrySendWorldInteraction(intent))
            {
                error = "The capability action request could not be sent.";
                return false;
            }

            State.Begin(operationId);
            return true;
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

            if (!State.TryOpen(opened, out var error))
            {
                simulationClient.DisconnectForClientFailure(
                    "world_interaction_state_conflict",
                    error);
            }
        }

        private void OnCompleted(RealtimeWorldInteractionResult result)
        {
            if (!State.TryComplete(result, out var error))
            {
                simulationClient.DisconnectForClientFailure(
                    "world_interaction_operation_conflict",
                    error);
            }
        }

        private void OnClosed(RealtimeWorldInteractionClosed closed)
        {
            State.Close(closed);
        }

        private void OnDisconnected(RealtimeClientError error)
        {
            State.Clear();
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
            simulationClient.UnexpectedlyDisconnected -= OnDisconnected;
            initialized = false;
        }
    }
}
