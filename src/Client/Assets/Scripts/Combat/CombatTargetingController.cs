using System;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Combat
{
    // Combat's world click handler, which sees every click before world interaction. A left
    // click on a living Mob, or on another player when the Shard allows player versus
    // player, makes it the attack target, and the character walks into weapon range. Any
    // other world click and Escape clear the target. While the character is dead every
    // world click ends here, and the navigator stops on death and on respawn, so a path
    // planned before death never runs from the spawn. The decisions live in
    // CombatClickRules, CombatTargetSelection, and CombatApproachPlanner.
    [DisallowMultipleComponent]
    public sealed class CombatTargetingController : MonoBehaviour, IWorldClickHandler
    {
        private readonly WorldPointerPicker picker = new WorldPointerPicker();
        private readonly CombatTargetSelection selection = new CombatTargetSelection();
        private readonly CombatApproachPlanner approach = new CombatApproachPlanner();
        private CombatClientController combatController;
        private RealtimeSimulationClient realtimeClient;
        private WorldActorClientController actorController;
        private InventoryClientController inventoryController;
        private CombatantViewLocator views;
        private LocalPlayerController localPlayer;
        private int approachIntentId;
        private ulong notifiedTarget;
        private bool subscribed;

        // Raised when the attack target or the latest rejection changes.
        public event Action Changed;

        // The target the character attacks or is about to attack, or 0 without one.
        public ulong TargetEntityId
        {
            get
            {
                return combatController != null
                    ? selection.EffectiveTarget(combatController.State.Owner)
                    : 0;
            }
        }

        // The error of the newest attack or clear request the worker rejected, or null.
        public RealtimeError LastRejection
        {
            get { return selection.LastRejection; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            CombatClientController combat,
            RealtimeSimulationClient realtime,
            WorldActorClientController worldActors,
            InventoryClientController inventory,
            CombatantViewLocator combatantViews)
        {
            combatController = combat;
            realtimeClient = realtime;
            actorController = worldActors;
            inventoryController = inventory;
            views = combatantViews;
        }

        private void OnEnable()
        {
            if (combatController == null || subscribed)
            {
                return;
            }

            combatController.State.IntentCompleted += OnIntentCompleted;
            combatController.State.Died += OnDied;
            combatController.State.Respawned += OnRespawned;
            subscribed = true;
        }

        private void OnDisable()
        {
            if (subscribed && combatController != null)
            {
                combatController.State.IntentCompleted -= OnIntentCompleted;
                combatController.State.Died -= OnDied;
                combatController.State.Respawned -= OnRespawned;
            }

            subscribed = false;
        }

        private void OnDestroy()
        {
            if (localPlayer != null)
            {
                localPlayer.RemoveClickHandler(this);
            }
        }

        private void Update()
        {
            EnsureLocalPlayer();
            if (localPlayer == null || combatController == null)
            {
                return;
            }

            // A request dropped with its session never gets a result.
            if (selection.PendingOperationId != Guid.Empty
                && !combatController.State.IsIntentPending(selection.PendingOperationId))
            {
                selection.Reset();
            }

            if (localPlayer.PlayerInput != null && localPlayer.PlayerInput.CancelPressedThisFrame)
            {
                ClearTarget();
            }

            UpdateApproach();
            NotifyIfTargetChanged();
        }

        public bool TryHandleWorldClick(Ray ray)
        {
            if (combatController == null)
            {
                return false;
            }

            var target = CombatClickRules.Describe(
                picker.Pick(ray, localPlayer != null ? localPlayer.transform : null),
                actorController != null ? actorController.State : null);
            combatController.State.TryGetCombatant(target.EntityId, out var targetState);
            var playerVersusPlayer = realtimeClient != null
                && realtimeClient.ShardRules != null
                && realtimeClient.ShardRules.PlayerVersusPlayer;
            switch (CombatClickRules.Classify(
                target,
                combatController.State.Owner,
                playerVersusPlayer,
                targetState))
            {
                case CombatClickAction.Attack:
                    Attack(target.EntityId);
                    return true;
                case CombatClickAction.Ignore:
                    return true;
                default:
                    ClearTarget();
                    return false;
            }
        }

        private void Attack(ulong targetEntityId)
        {
            // Clicking the current target again only walks into range again.
            if (targetEntityId == TargetEntityId && selection.PendingOperationId == Guid.Empty)
            {
                approach.Reset();
                return;
            }

            if (!combatController.TryAttackTarget(targetEntityId, out var operationId, out var error))
            {
                LogNotSent(error);
                return;
            }

            selection.BeginAttack(operationId, targetEntityId);
            NotifyIfTargetChanged();
        }

        // Sends ClearTarget only when there is a target or a request to undo.
        private void ClearTarget()
        {
            var owner = combatController.State.Owner;
            if (TargetEntityId == 0
                && selection.PendingOperationId == Guid.Empty
                && (owner == null || owner.TargetEntityId == 0))
            {
                return;
            }

            if (!combatController.TryClearTarget(out var operationId, out var error))
            {
                LogNotSent(error);
                return;
            }

            selection.BeginClear(operationId);
            NotifyIfTargetChanged();
        }

        private void UpdateApproach()
        {
            var owner = combatController.State.Owner;
            if (CombatClickRules.IsDead(owner)
                || !localPlayer.TryGetAuthoritativePosition(out var position))
            {
                return;
            }

            var target = TargetEntityId;
            float? weaponRange = null;
            if (inventoryController != null
                && CombatLoadoutRules.TryGetWeaponRange(
                    inventoryController.State.Catalog,
                    inventoryController.State.FullSnapshot,
                    out var range))
            {
                weaponRange = range;
            }

            Vector3? targetPosition = null;
            if (views != null && views.TryGetView(target, out var view))
            {
                targetPosition = view.Position;
            }

            var ownsNavigation = approachIntentId != 0
                && localPlayer.NavigationIntentId == approachIntentId;
            var isMoving = localPlayer.IsFollowingPath
                || (ownsNavigation && localPlayer.LatestNavigationResult == NavigationResult.Planning);
            RealtimeCombatAttackStatus? status = null;
            if (owner != null && target != 0 && owner.TargetEntityId == target)
            {
                status = owner.AttackStatus;
            }

            var decision = approach.Update(new CombatApproachInput(
                target,
                weaponRange,
                position,
                targetPosition,
                isMoving,
                ownsNavigation,
                status,
                Time.unscaledTime));
            switch (decision.Action)
            {
                case CombatApproachAction.Approach:
                    approachIntentId = localPlayer.Approach(targetPosition.Value, decision.Range);
                    break;
                case CombatApproachAction.Stop:
                    localPlayer.StopMoving();
                    approachIntentId = 0;
                    break;
            }
        }

        private void OnIntentCompleted(RealtimeCombatIntentResult result)
        {
            if (!selection.ApplyResult(result, combatController.State.Owner))
            {
                return;
            }

            if (!result.Succeeded && result.Error != null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "The worker rejected the combat request: " + result.Error.Message);
            }

            Changed?.Invoke();
        }

        // The worker cleared the target and halted the character; a path planned before
        // the death must not run after it.
        private void OnDied(RealtimeOwnerCombatState owner)
        {
            selection.Reset();
            StopNavigation();
            Changed?.Invoke();
        }

        private void OnRespawned(RealtimeOwnerCombatState owner)
        {
            StopNavigation();
        }

        private void StopNavigation()
        {
            approach.Reset();
            approachIntentId = 0;
            if (localPlayer != null)
            {
                localPlayer.StopMoving();
            }
        }

        private void NotifyIfTargetChanged()
        {
            var target = TargetEntityId;
            if (target == notifiedTarget)
            {
                return;
            }

            notifiedTarget = target;
            Changed?.Invoke();
        }

        private void EnsureLocalPlayer()
        {
            var player = views != null ? views.LocalPlayer : null;
            if (player == localPlayer)
            {
                return;
            }

            if (localPlayer != null)
            {
                localPlayer.RemoveClickHandler(this);
            }

            localPlayer = player;
            approach.Reset();
            approachIntentId = 0;
            if (localPlayer != null)
            {
                localPlayer.AddClickHandler(this, WorldClickOrder.Combat);
            }
        }

        private static void LogNotSent(string error)
        {
            ClientLog.Warning(ClientLogCategory.Client, "Combat request was not sent: " + error);
        }
    }
}
