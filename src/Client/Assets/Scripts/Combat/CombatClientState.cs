using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Combat
{
    // Mirrors the worker's combat state: one state per visible combatant, the owner's own
    // combat state, and the combat intents that wait for a result. The worker owns health,
    // targets, attacks, and death; nothing here is predicted or decided by the client.
    public sealed class CombatClientState
    {
        // The worker answers every intent, so this only bounds a client that sends faster
        // than results return.
        public const int MaximumPendingIntents = 8;

        private readonly Dictionary<ulong, RealtimeCombatantState> combatants =
            new Dictionary<ulong, RealtimeCombatantState>();
        private readonly Dictionary<Guid, RealtimeCombatIntent> pendingIntents =
            new Dictionary<Guid, RealtimeCombatIntent>();

        public event Action Changed;

        // Combat events in their simulation order, for presentation only.
        public event Action<RealtimeCombatEventBatch> EventsReceived;

        public event Action<RealtimeCombatIntentResult> IntentCompleted;

        // Raised when the owner dies. The worker has stopped its movement and cleared its
        // target; it respawns at the respawn tick.
        public event Action<RealtimeOwnerCombatState> Died;

        // Raised when the owner lives again after a death. The worker has moved the
        // character to the World spawn with full health.
        public event Action<RealtimeOwnerCombatState> Respawned;

        public RealtimeOwnerCombatState Owner { get; private set; }

        public RealtimeCombatIntentResult LastIntentResult { get; private set; }

        public int PendingIntentCount
        {
            get { return pendingIntents.Count; }
        }

        public IReadOnlyList<RealtimeCombatantState> Combatants
        {
            get { return combatants.Values.OrderBy(state => state.EntityId).ToArray(); }
        }

        public bool TryGetCombatant(ulong entityId, out RealtimeCombatantState state)
        {
            return combatants.TryGetValue(entityId, out state);
        }

        public bool IsIntentPending(Guid operationId)
        {
            return pendingIntents.ContainsKey(operationId);
        }

        // Applies a newer combatant state and ignores an older one. Equal revisions must
        // carry equal values.
        public bool TryApplyCombatant(RealtimeCombatantState state, out string error)
        {
            error = string.Empty;
            if (state == null)
            {
                error = "Combatant state is required.";
                return false;
            }

            if (combatants.TryGetValue(state.EntityId, out var current))
            {
                if (state.StateRevision < current.StateRevision)
                {
                    return true;
                }

                if (state.StateRevision == current.StateRevision)
                {
                    if (!HasSameValues(current, state))
                    {
                        error = "Combatant state conflicts with the current revision.";
                        return false;
                    }

                    return true;
                }
            }

            combatants[state.EntityId] = state;
            Changed?.Invoke();
            return true;
        }

        // Applies a newer owner state and ignores an older one. Equal revisions must carry
        // equal values; only the server tick of a repeated state may differ.
        public bool TryApplyOwner(RealtimeOwnerCombatState state, out string error)
        {
            error = string.Empty;
            if (state == null)
            {
                error = "Owner combat state is required.";
                return false;
            }

            var previous = Owner;
            if (previous != null)
            {
                if (state.StateRevision < previous.StateRevision)
                {
                    return true;
                }

                if (state.StateRevision == previous.StateRevision)
                {
                    if (!HasSameValues(previous, state))
                    {
                        error = "Owner combat state conflicts with the current revision.";
                        return false;
                    }

                    return true;
                }
            }

            Owner = state;
            Changed?.Invoke();
            if (state.LifeState == RealtimeCombatLifeState.Dead
                && (previous == null
                    || previous.LifeState != RealtimeCombatLifeState.Dead
                    || previous.LifeSequence != state.LifeSequence))
            {
                Died?.Invoke(state);
            }
            else if (previous != null
                && state.LifeSequence > previous.LifeSequence
                && state.LifeState == RealtimeCombatLifeState.Alive)
            {
                Respawned?.Invoke(state);
            }

            return true;
        }

        // A combatant that left the recipient's interest has no state until it spawns again.
        public void RemoveCombatant(ulong entityId)
        {
            if (combatants.Remove(entityId))
            {
                Changed?.Invoke();
            }
        }

        public void ApplyEvents(RealtimeCombatEventBatch batch)
        {
            if (batch != null)
            {
                EventsReceived?.Invoke(batch);
            }
        }

        public bool TryBeginIntent(RealtimeCombatIntent intent, out string error)
        {
            error = string.Empty;
            if (intent == null || intent.OperationId == Guid.Empty)
            {
                error = "A combat intent with an operation id is required.";
                return false;
            }

            if (pendingIntents.Count >= MaximumPendingIntents)
            {
                error = "Too many combat requests are waiting for the server.";
                return false;
            }

            if (pendingIntents.ContainsKey(intent.OperationId))
            {
                error = "The combat intent is already pending.";
                return false;
            }

            pendingIntents.Add(intent.OperationId, intent);
            Changed?.Invoke();
            return true;
        }

        // Forgets an intent that could not be sent.
        public void CancelIntent(Guid operationId)
        {
            if (pendingIntents.Remove(operationId))
            {
                Changed?.Invoke();
            }
        }

        public bool TryCompleteIntent(RealtimeCombatIntentResult result, out string error)
        {
            error = string.Empty;
            if (result == null
                || !pendingIntents.TryGetValue(result.OperationId, out var intent))
            {
                error = "Combat intent result does not match a pending intent.";
                return false;
            }

            if (intent.Kind != result.Kind)
            {
                error = "Combat intent result kind does not match the pending intent.";
                return false;
            }

            pendingIntents.Remove(result.OperationId);
            LastIntentResult = result;
            Changed?.Invoke();
            IntentCompleted?.Invoke(result);
            return true;
        }

        // Drops all session state. Revisions start again in every session.
        public void Clear()
        {
            if (combatants.Count == 0
                && pendingIntents.Count == 0
                && Owner == null
                && LastIntentResult == null)
            {
                return;
            }

            combatants.Clear();
            pendingIntents.Clear();
            Owner = null;
            LastIntentResult = null;
            Changed?.Invoke();
        }

        private static bool HasSameValues(
            RealtimeCombatantState first,
            RealtimeCombatantState second)
        {
            return first.Health == second.Health
                && first.MaximumHealth == second.MaximumHealth
                && first.LifeState == second.LifeState
                && first.InCombat == second.InCombat
                && first.TargetEntityId == second.TargetEntityId;
        }

        private static bool HasSameValues(
            RealtimeOwnerCombatState first,
            RealtimeOwnerCombatState second)
        {
            return first.Health == second.Health
                && first.MaximumHealth == second.MaximumHealth
                && first.LifeState == second.LifeState
                && first.LifeSequence == second.LifeSequence
                && first.RespawnTick == second.RespawnTick
                && first.InCombat == second.InCombat
                && first.TargetEntityId == second.TargetEntityId
                && first.AttackStatus == second.AttackStatus
                && first.NextAttackTick == second.NextAttackTick
                && first.LockoutEndTick == second.LockoutEndTick
                && first.AmmunitionItemInstanceId == second.AmmunitionItemInstanceId
                && first.AmmunitionQuantity == second.AmmunitionQuantity;
        }
    }
}
