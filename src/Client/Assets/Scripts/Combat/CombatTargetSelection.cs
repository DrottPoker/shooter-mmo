using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Combat
{
    // The attack target the client acts on. The worker owns the target, so this is the
    // owner state's target, except while a request is on its way: then it is the requested
    // target, and after a confirmation it stays the confirmed target until a newer owner
    // state arrives, because the result can arrive before the owner state that shows it.
    public sealed class CombatTargetSelection
    {
        private Guid pendingOperationId;
        private ulong pendingTarget;
        private bool awaitingOwnerState;
        private ulong confirmedTarget;
        private long confirmedOwnerRevision;

        public Guid PendingOperationId
        {
            get { return pendingOperationId; }
        }

        // The error of the newest request the worker rejected, or null.
        public RealtimeError LastRejection { get; private set; }

        public void BeginAttack(Guid operationId, ulong targetEntityId)
        {
            Begin(operationId, targetEntityId);
        }

        public void BeginClear(Guid operationId)
        {
            Begin(operationId, 0);
        }

        // Applies the result of the newest request; a result of an older request changes
        // nothing, because a newer request already replaced it. Returns true when the
        // result belonged to the newest request.
        public bool ApplyResult(RealtimeCombatIntentResult result, RealtimeOwnerCombatState owner)
        {
            if (result == null
                || pendingOperationId == Guid.Empty
                || result.OperationId != pendingOperationId)
            {
                return false;
            }

            pendingOperationId = Guid.Empty;
            pendingTarget = 0;
            LastRejection = result.Succeeded ? null : result.Error;
            awaitingOwnerState = result.Succeeded;
            confirmedTarget = result.TargetEntityId;
            confirmedOwnerRevision = owner != null ? owner.StateRevision : -1;
            return true;
        }

        public ulong EffectiveTarget(RealtimeOwnerCombatState owner)
        {
            if (pendingOperationId != Guid.Empty)
            {
                return pendingTarget;
            }

            if (awaitingOwnerState
                && (owner == null || owner.StateRevision <= confirmedOwnerRevision))
            {
                return confirmedTarget;
            }

            return owner != null ? owner.TargetEntityId : 0;
        }

        // Drops the session's requests, such as after a death or a reconnect.
        public void Reset()
        {
            pendingOperationId = Guid.Empty;
            pendingTarget = 0;
            awaitingOwnerState = false;
            confirmedTarget = 0;
            confirmedOwnerRevision = -1;
            LastRejection = null;
        }

        private void Begin(Guid operationId, ulong targetEntityId)
        {
            pendingOperationId = operationId;
            pendingTarget = targetEntityId;
            awaitingOwnerState = false;
            confirmedTarget = 0;
            LastRejection = null;
        }
    }
}
