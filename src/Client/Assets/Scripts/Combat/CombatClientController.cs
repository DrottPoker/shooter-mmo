using System;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Combat
{
    // Persistent adapter between the realtime client and the combat state. It sends attack
    // and clear intents and applies the worker's combat messages; presentation reads State.
    [DisallowMultipleComponent]
    public sealed class CombatClientController : MonoBehaviour
    {
        private RealtimeSimulationClient simulationClient;
        private bool initialized;

        public CombatClientState State { get; } = new CombatClientState();

        public void Initialize(RealtimeSimulationClient realtimeClient)
        {
            if (realtimeClient == null)
            {
                throw new ArgumentNullException(nameof(realtimeClient));
            }

            if (initialized)
            {
                Unsubscribe();
            }

            simulationClient = realtimeClient;
            simulationClient.SessionJoined += OnSessionJoined;
            simulationClient.SessionEnded += OnSessionEnded;
            simulationClient.CombatIntentCompleted += OnIntentCompleted;
            simulationClient.CombatantStateReceived += OnCombatantState;
            simulationClient.CombatEventBatchReceived += OnEventBatch;
            simulationClient.OwnerCombatStateReceived += OnOwnerState;
            simulationClient.EntityDespawned += OnEntityDespawned;
            simulationClient.WorldActorDespawned += OnWorldActorDespawned;
            initialized = true;
            State.Clear();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        // Selects the target the character attacks until it is cleared, dies, or despawns.
        // The worker checks every rule; these checks only avoid a request that must fail.
        // The operation id names the result that answers the request.
        public bool TryAttackTarget(ulong targetEntityId, out Guid operationId, out string error)
        {
            operationId = Guid.Empty;
            if (!CanSend(out error))
            {
                return false;
            }

            if (targetEntityId == 0)
            {
                error = "A combat target is required.";
                return false;
            }

            if (targetEntityId == simulationClient.MovementSession.ControlledEntityId)
            {
                error = "The character cannot attack itself.";
                return false;
            }

            if (State.Owner != null && State.Owner.LifeState == RealtimeCombatLifeState.Dead)
            {
                error = "The character is dead.";
                return false;
            }

            var intent = RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId);
            if (!TrySend(intent, out error))
            {
                return false;
            }

            operationId = intent.OperationId;
            return true;
        }

        public bool TryClearTarget(out Guid operationId, out string error)
        {
            operationId = Guid.Empty;
            if (!CanSend(out error))
            {
                return false;
            }

            var intent = RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid());
            if (!TrySend(intent, out error))
            {
                return false;
            }

            operationId = intent.OperationId;
            return true;
        }

        private bool CanSend(out string error)
        {
            error = string.Empty;
            if (!initialized
                || simulationClient == null
                || !simulationClient.IsJoined
                || simulationClient.MovementSession == null)
            {
                error = "Combat requires an active simulation connection.";
                return false;
            }

            return true;
        }

        private bool TrySend(RealtimeCombatIntent intent, out string error)
        {
            if (!State.TryBeginIntent(intent, out error))
            {
                return false;
            }

            if (!simulationClient.TrySendCombatIntent(intent))
            {
                State.CancelIntent(intent.OperationId);
                error = "The combat request could not be sent.";
                return false;
            }

            return true;
        }

        private void OnIntentCompleted(RealtimeCombatIntentResult result)
        {
            if (!State.TryCompleteIntent(result, out var error))
            {
                simulationClient.DisconnectForClientFailure(
                    "combat_intent_result_mismatch",
                    error);
            }
        }

        private void OnCombatantState(RealtimeCombatantState state)
        {
            if (!State.TryApplyCombatant(state, out var error))
            {
                simulationClient.DisconnectForClientFailure("combat_state_conflict", error);
            }
        }

        private void OnOwnerState(RealtimeOwnerCombatState state)
        {
            if (!State.TryApplyOwner(state, out var error))
            {
                simulationClient.DisconnectForClientFailure("combat_state_conflict", error);
            }
        }

        private void OnEventBatch(RealtimeCombatEventBatch batch)
        {
            State.ApplyEvents(batch);
        }

        private void OnEntityDespawned(RealtimeEntityDespawn despawn)
        {
            State.RemoveCombatant(despawn.EntityId);
        }

        private void OnWorldActorDespawned(RealtimeWorldActorDespawn despawn)
        {
            State.RemoveCombatant(despawn.EntityId);
        }

        private void OnSessionJoined()
        {
            State.Clear();
        }

        private void OnSessionEnded(RealtimeClientError error)
        {
            State.Clear();
        }

        private void Unsubscribe()
        {
            if (!initialized || simulationClient == null)
            {
                return;
            }

            simulationClient.SessionJoined -= OnSessionJoined;
            simulationClient.SessionEnded -= OnSessionEnded;
            simulationClient.CombatIntentCompleted -= OnIntentCompleted;
            simulationClient.CombatantStateReceived -= OnCombatantState;
            simulationClient.CombatEventBatchReceived -= OnEventBatch;
            simulationClient.OwnerCombatStateReceived -= OnOwnerState;
            simulationClient.EntityDespawned -= OnEntityDespawned;
            simulationClient.WorldActorDespawned -= OnWorldActorDespawned;
            initialized = false;
        }
    }
}
