using System;
using System.Collections.Generic;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Actors;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    public readonly struct WorldInteractionTargetCandidate
    {
        public WorldInteractionTargetCandidate(
            IWorldInteractionTarget target,
            float distance)
        {
            Target = target;
            Distance = distance;
        }

        public IWorldInteractionTarget Target { get; }
        public float Distance { get; }
    }

    // The pointer picks NPCs and corpses. A left click on one walks the player into range
    // and then asks the worker to open the interaction, and Escape closes it. The worker
    // still owns range, line of sight, revision, and session validation.
    [DisallowMultipleComponent]
    public sealed class WorldInteractionTargetingController : MonoBehaviour, IWorldClickHandler
    {
        private const float PointerRange = WorldInteractionRules.ClientPointerRange;
        private const float SpherecastRadius = WorldInteractionRules.ClientSpherecastRadius;

        // Inside the worker's start range, so the center distance already proves the
        // worker's closest-point distance.
        private const float OpenRange = WorldInteractionRules.AuthoritativeStartRange - 0.25f;

        // A walk that ended out of range stops waiting after this long.
        private const float ApproachSettleSeconds = 1.5f;

        private readonly RaycastHit[] directHits = new RaycastHit[32];
        private readonly RaycastHit[] sphereHits = new RaycastHit[32];
        private readonly List<WorldInteractionTargetCandidate> directCandidates =
            new List<WorldInteractionTargetCandidate>();
        private readonly List<WorldInteractionTargetCandidate> sphereCandidates =
            new List<WorldInteractionTargetCandidate>();
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

        public static IWorldInteractionTarget SelectCandidate(
            IReadOnlyList<WorldInteractionTargetCandidate> direct,
            IReadOnlyList<WorldInteractionTargetCandidate> spherecast)
        {
            var selected = ClosestActive(direct);
            return selected ?? ClosestActive(spherecast);
        }

        public static IWorldInteractionTarget FindRegisteredTarget(Collider collider)
        {
            if (collider == null)
            {
                return null;
            }

            var behaviours = collider.GetComponentsInParent<MonoBehaviour>(true);
            for (var index = 0; index < behaviours.Length; index++)
            {
                if (behaviours[index] is IWorldInteractionTarget target)
                {
                    return target;
                }
            }

            return null;
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

        private IWorldInteractionTarget PickTarget(Ray ray)
        {
            directCandidates.Clear();
            sphereCandidates.Clear();
            var directCount = Physics.RaycastNonAlloc(
                ray,
                directHits,
                PointerRange,
                Physics.AllLayers,
                QueryTriggerInteraction.Collide);
            AddCandidates(directHits, directCount, directCandidates);
            if (directCandidates.Count == 0)
            {
                var sphereCount = Physics.SphereCastNonAlloc(
                    ray,
                    SpherecastRadius,
                    sphereHits,
                    PointerRange,
                    Physics.AllLayers,
                    QueryTriggerInteraction.Collide);
                AddCandidates(sphereHits, sphereCount, sphereCandidates);
            }

            return SelectCandidate(directCandidates, sphereCandidates);
        }

        private static IWorldInteractionTarget ClosestActive(
            IReadOnlyList<WorldInteractionTargetCandidate> values)
        {
            IWorldInteractionTarget selected = null;
            var selectedDistance = float.PositiveInfinity;
            for (var index = 0; index < values.Count; index++)
            {
                var candidate = values[index];
                if (candidate.Target == null
                    || !candidate.Target.IsTargetActive
                    || candidate.Distance < 0f
                    || candidate.Distance >= selectedDistance)
                {
                    continue;
                }

                selected = candidate.Target;
                selectedDistance = candidate.Distance;
            }

            return selected;
        }

        // A target counts only when no solid collider stands between it and the camera.
        private void AddCandidates(
            RaycastHit[] hits,
            int count,
            ICollection<WorldInteractionTargetCandidate> destination)
        {
            var nearestBlockerDistance = float.PositiveInfinity;
            for (var index = 0; index < count; index++)
            {
                var collider = hits[index].collider;
                if (collider != null
                    && !collider.isTrigger
                    && !IsLocalPlayer(collider)
                    && FindRegisteredTarget(collider) == null)
                {
                    nearestBlockerDistance = Mathf.Min(
                        nearestBlockerDistance,
                        hits[index].distance);
                }
            }

            for (var index = 0; index < count; index++)
            {
                var target = FindRegisteredTarget(hits[index].collider);
                if (target != null
                    && hits[index].distance <= nearestBlockerDistance + 0.001f)
                {
                    destination.Add(new WorldInteractionTargetCandidate(
                        target,
                        hits[index].distance));
                }
            }
        }

        private bool IsLocalPlayer(Collider collider)
        {
            return localPlayer != null && collider.transform.IsChildOf(localPlayer.transform);
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
                localPlayer.AddClickHandler(this);
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
