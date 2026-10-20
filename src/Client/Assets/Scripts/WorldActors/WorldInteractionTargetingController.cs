using System;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Actors;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    // World interaction's part of the world menu. An NPC or resource node offers one option per
    // capability, in authored order, such as Talk-to, Trade, or Mine, and a corpse offers Loot;
    // a Mob belongs to combat. Choosing one walks the player into range, asks the worker to
    // open the interaction, and then starts the chosen capability. Escape closes it. The
    // worker still owns range, line of sight, revision, session, and availability validation.
    [DisallowMultipleComponent]
    public sealed class WorldInteractionTargetingController : MonoBehaviour, IWorldMenuProvider
    {
        private const int OpenCapabilityAction = 1;
        private const int LootCorpseAction = 2;

        // Inside the worker's start range, so the center distance already proves the
        // worker's closest-point distance.
        public const float OpenRange = WorldInteractionRules.AuthoritativeStartRange - 0.25f;

        // The walk aims closer than the open check, because a route ends within its range
        // plus the waypoint arrival tolerance, so a walk to exactly OpenRange can stop a
        // hair outside it.
        public const float ApproachRange = OpenRange - 0.25f;

        // A walk that ended out of range stops waiting after this long.
        private const float ApproachSettleSeconds = 1.5f;

        private LocalPlayerController localPlayer;
        private WorldInteractionClientController interactionController;
        private WorldActorClientController actorController;
        private WorldMenuController menu;
        private GameMessageLog messages;
        private IWorldInteractionTarget approachTarget;
        private string approachCapabilityId = string.Empty;
        private int approachIntentId;
        private float approachEndedAt = -1f;

        public event Action Changed;

        // The target the player walks to before the interaction opens.
        public IWorldInteractionTarget ApproachTarget
        {
            get { return approachTarget; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            WorldInteractionClientController worldInteraction,
            WorldActorClientController worldActors,
            WorldMenuController worldMenu,
            GameMessageLog gameMessages)
        {
            interactionController = worldInteraction;
            actorController = worldActors;
            menu = worldMenu;
            messages = gameMessages;
        }

        private void OnEnable()
        {
            if (menu != null)
            {
                menu.AddProvider(this, WorldMenuOrder.WorldInteraction);
            }
        }

        private void OnDisable()
        {
            if (menu != null)
            {
                menu.RemoveProvider(this);
            }
        }

        private void Update()
        {
            EnsureLocalPlayer();
            if (localPlayer == null || localPlayer.PlayerInput == null)
            {
                return;
            }

            if (localPlayer.PlayerInput.CancelPressedThisFrame
                && (menu == null || !menu.CancelConsumedThisFrame))
            {
                CancelApproach();
                if (interactionController != null && interactionController.HasActiveLease)
                {
                    SubmitCloseActive();
                }
            }

            UpdateApproach();
        }

        public void AddWorldMenuOptions(WorldMenuBuilder worldMenu)
        {
            var candidates = worldMenu.Candidates;
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var target = candidate.Target;
                if (target == null)
                {
                    continue;
                }

                if (target.TargetKind == RealtimeWorldInteractionTargetKind.Corpse)
                {
                    worldMenu.Add(WorldMenuOption.ForTarget(
                        this,
                        LootCorpseAction,
                        index,
                        candidate,
                        "Loot",
                        WorldMenuTargets.CorpseName(target.TargetDisplayName),
                        WorldMenuTargetStyle.Corpse));
                    continue;
                }

                if (actorController == null
                    || !actorController.State.TryGet(target.TargetEntityId, out var actor)
                    || !OffersInteraction(target.TargetKind, actor.Kind))
                {
                    continue;
                }

                for (var optionIndex = 0; optionIndex < actor.MenuOptions.Count; optionIndex++)
                {
                    var option = actor.MenuOptions[optionIndex];
                    worldMenu.Add(WorldMenuOption.ForTarget(
                        this,
                        OpenCapabilityAction,
                        index,
                        candidate,
                        option.Label,
                        actor.DisplayName,
                        WorldMenuTargets.StyleOf(actor.Kind),
                        option.CapabilityId));
                }
            }
        }

        public void ExecuteWorldMenuOption(WorldMenuOption option)
        {
            var target = option.Candidate.Target;
            if (target == null || !target.IsTargetActive || IsDestroyed(target))
            {
                return;
            }

            Interact(
                target,
                option.Action == OpenCapabilityAction ? option.Argument : string.Empty);
        }

        // Corpses, NPCs, and resource nodes offer an interaction. A Mob does not: its option
        // belongs to combat, and an unknown actor is no target.
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

        private void Interact(IWorldInteractionTarget target, string capabilityId)
        {
            if (interactionController == null || localPlayer == null)
            {
                return;
            }

            if (interactionController.HasActiveLease)
            {
                // The open interaction runs the capability without walking or reopening.
                if (IsActiveTarget(target))
                {
                    CancelApproach();
                    interactionController.ActivateCapability(capabilityId);
                    return;
                }

                // Opening another target first closes the current interaction.
                SubmitCloseActive();
            }

            var intentId = localPlayer.Approach(target.TargetPosition, ApproachRange);
            if (intentId == 0)
            {
                CancelApproach();
                return;
            }

            approachTarget = target;
            approachCapabilityId = capabilityId ?? string.Empty;
            approachIntentId = intentId;
            approachEndedAt = -1f;
            Changed?.Invoke();
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
                // The target left, or a new option sent the player somewhere else.
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
                ReportUnreachable("There is no way to reach " + approachTarget.TargetDisplayName + ".");
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
                var capabilityId = approachCapabilityId;
                CancelApproach();
                SubmitOpen(opened, capabilityId);
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
                ReportUnreachable("You can't get close enough to " + approachTarget.TargetDisplayName + ".");
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
            approachCapabilityId = string.Empty;
            approachIntentId = 0;
            approachEndedAt = -1f;
            Changed?.Invoke();
        }

        private void SubmitOpen(IWorldInteractionTarget target, string capabilityId)
        {
            if (target.TargetKind == RealtimeWorldInteractionTargetKind.WorldActor)
            {
                if (actorController != null
                    && actorController.State.TryGet(target.TargetEntityId, out var actor)
                    && !interactionController.TryOpenActor(actor, capabilityId, out var actorError))
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

        private void EnsureLocalPlayer()
        {
            if (localPlayer == null)
            {
                localPlayer = FindAnyObjectByType<LocalPlayerController>();
            }
        }

        private void ReportUnreachable(string message)
        {
            messages?.Add(message, Time.unscaledTime);
            LogSubmissionFailure(message);
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
