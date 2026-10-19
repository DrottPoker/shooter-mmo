using System;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Actors;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    // The pointer picks NPCs, resource nodes, and corpses; a Mob belongs to combat. A left
    // click on one walks the player into range and then asks the worker to open the
    // interaction, and Escape closes it. The worker still owns range, line of sight,
    // revision, and session validation.
    [DisallowMultipleComponent]
    public sealed class WorldInteractionTargetingController : MonoBehaviour, IWorldClickHandler
    {
        // Inside the worker's start range, so the center distance already proves the
        // worker's closest-point distance.
        private const float OpenRange = WorldInteractionRules.AuthoritativeStartRange - 0.25f;

        // A walk that ended out of range stops waiting after this long.
        private const float ApproachSettleSeconds = 1.5f;

        private readonly WorldPointerPicker picker = new WorldPointerPicker();
        private LocalPlayerController localPlayer;
        private WorldInteractionClientController interactionController;
        private WorldActorClientController actorController;
        private IWorldInteractionTarget approachTarget;
        private int approachIntentId;
        private float approachEndedAt = -1f;

        public event Action Changed;

        // The target under the pointer.
        public IWorldInteractionTarget CurrentTarget { get; private set; }

        // The target the player walks to before the interaction opens.
        public IWorldInteractionTarget ApproachTarget
        {
            get { return approachTarget; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            WorldInteractionClientController worldInteraction,
            WorldActorClientController worldActors)
        {
            interactionController = worldInteraction;
            actorController = worldActors;
        }

        private void Update()
        {
            EnsureLocalPlayer();
            UpdateHoverTarget();
            if (localPlayer == null || localPlayer.PlayerInput == null)
            {
                return;
            }

            if (localPlayer.PlayerInput.CancelPressedThisFrame)
            {
                CancelApproach();
                if (interactionController != null && interactionController.HasActiveLease)
                {
                    SubmitCloseActive();
                }
            }

            UpdateApproach();
        }

        private void OnDestroy()
        {
            if (localPlayer != null)
            {
                localPlayer.RemoveClickHandler(this);
            }
        }

        public bool TryHandleWorldClick(Ray ray)
        {
            var target = PickTarget(ray);
            if (target == null)
            {
                return false;
            }

            if (interactionController == null || localPlayer == null)
            {
                return true;
            }

            if (interactionController.HasActiveLease)
            {
                if (IsActiveTarget(target))
                {
                    return true;
                }

                // Opening another target first closes the current interaction.
                SubmitCloseActive();
            }

            var intentId = localPlayer.Approach(target.TargetPosition, OpenRange);
            if (intentId == 0)
            {
                CancelApproach();
                return true;
            }

            approachTarget = target;
            approachIntentId = intentId;
            approachEndedAt = -1f;
            Changed?.Invoke();
            return true;
        }

        // Corpses, NPCs, and resource nodes offer an interaction. A Mob does not: a click on
        // it belongs to combat, and an unknown actor is no target.
        public static bool OffersInteraction(
            RealtimeWorldInteractionTargetKind targetKind,
            RealtimeWorldActorKind? actorKind)
        {
            if (targetKind == RealtimeWorldInteractionTargetKind.Corpse)
            {
                return true;
            }

            return targetKind == RealtimeWorldInteractionTargetKind.WorldActor
                && actorKind.HasValue
                && actorKind.Value != RealtimeWorldActorKind.Mob;
        }

        private void UpdateApproach()
        {
            if (approachTarget == null || interactionController == null)
            {
                return;
            }

            if (!approachTarget.IsTargetActive
                || IsDestroyed(approachTarget)
                || localPlayer.NavigationIntentId != approachIntentId)
            {
                // The target left, or a new click sent the player somewhere else.
                CancelApproach();
                return;
            }

            var navigation = localPlayer.LatestNavigationResult;
            if (navigation == NavigationResult.Planning)
            {
                return;
            }

            if (navigation == NavigationResult.NoRoute)
            {
                LogSubmissionFailure("There is no way to reach " + approachTarget.TargetDisplayName + ".");
                CancelApproach();
                return;
            }

            if (interactionController.HasActiveLease
                || interactionController.State.PendingOperationId != Guid.Empty
                || !localPlayer.TryGetAuthoritativePosition(out var position))
            {
                return;
            }

            var target = approachTarget.TargetPosition;
            var deltaX = position.x - target.x;
            var deltaZ = position.z - target.z;
            if ((deltaX * deltaX) + (deltaZ * deltaZ) <= OpenRange * OpenRange)
            {
                var opened = approachTarget;
                CancelApproach();
                SubmitOpen(opened);
                return;
            }

            if (localPlayer.IsFollowingPath)
            {
                approachEndedAt = -1f;
                return;
            }

            if (approachEndedAt < 0f)
            {
                approachEndedAt = Time.unscaledTime;
            }
            else if (Time.unscaledTime - approachEndedAt > ApproachSettleSeconds)
            {
                LogSubmissionFailure("Could not get close enough to " + approachTarget.TargetDisplayName + ".");
                CancelApproach();
            }
        }

        private void CancelApproach()
        {
            if (approachTarget == null)
            {
                return;
            }

            approachTarget = null;
            approachIntentId = 0;
            approachEndedAt = -1f;
            Changed?.Invoke();
        }

        private void SubmitOpen(IWorldInteractionTarget target)
        {
            if (target.TargetKind == RealtimeWorldInteractionTargetKind.WorldActor)
            {
                if (actorController != null
                    && actorController.State.TryGet(target.TargetEntityId, out var actor)
                    && !interactionController.TryOpenActor(actor, out var actorError))
                {
                    LogSubmissionFailure(actorError);
                }

                return;
            }

            if (!interactionController.TryOpenCorpse(target.TargetRuntimeId, out var error))
            {
                LogSubmissionFailure(error);
            }
        }

        private void SubmitCloseActive()
        {
            if (!interactionController.TryCloseActive(out var error))
            {
                LogSubmissionFailure(error);
            }
        }

        private bool IsActiveTarget(IWorldInteractionTarget target)
        {
            var active = interactionController.State.ActiveInteraction;
            return active != null && active.TargetRuntimeId == target.TargetRuntimeId;
        }

        private void UpdateHoverTarget()
        {
            if (localPlayer == null || !localPlayer.TryGetPointerRay(out var ray))
            {
                SetCurrentTarget(null);
                return;
            }

            SetCurrentTarget(PickTarget(ray));
        }

        // The nearest target under the pointer when it offers an interaction. A nearer Mob or
        // player hides the target behind it, as it does for combat.
        private IWorldInteractionTarget PickTarget(Ray ray)
        {
            var candidate = picker.Pick(ray, localPlayer != null ? localPlayer.transform : null);
            var target = candidate.Target;
            if (target == null)
            {
                return null;
            }

            RealtimeWorldActorKind? actorKind = null;
            if (target.TargetKind == RealtimeWorldInteractionTargetKind.WorldActor
                && actorController != null
                && actorController.State.TryGet(target.TargetEntityId, out var actor))
            {
                actorKind = actor.Kind;
            }

            return OffersInteraction(target.TargetKind, actorKind) ? target : null;
        }

        private void SetCurrentTarget(IWorldInteractionTarget target)
        {
            if (ReferenceEquals(CurrentTarget, target))
            {
                return;
            }

            CurrentTarget = target;
            Changed?.Invoke();
        }

        private void EnsureLocalPlayer()
        {
            if (localPlayer != null)
            {
                return;
            }

            localPlayer = FindAnyObjectByType<LocalPlayerController>();
            if (localPlayer != null)
            {
                localPlayer.AddClickHandler(this, WorldClickOrder.WorldInteraction);
            }
        }

        private static bool IsDestroyed(IWorldInteractionTarget target)
        {
            return target is UnityEngine.Object unityObject && unityObject == null;
        }

        private static void LogSubmissionFailure(string error)
        {
            ClientLog.Warning(
                ClientLogCategory.Client,
                "World interaction was not submitted: " + error);
        }
    }
}
