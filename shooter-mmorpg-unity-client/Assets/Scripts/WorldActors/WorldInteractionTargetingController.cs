using System;
using System.Collections.Generic;
using ShooterMmo.Diagnostics;
using ShooterMmo.GameProtocol;
using ShooterMmo.Gameplay;
using ShooterMmo.WorldData.Actors;
using UnityEngine;

namespace ShooterMmo.WorldActors
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

    [DisallowMultipleComponent]
    public sealed class WorldInteractionTargetingController : MonoBehaviour
    {
        private const float DiscoveryRange = WorldInteractionRules.ClientDiscoveryRange;
        private const float SpherecastRadius = WorldInteractionRules.ClientSpherecastRadius;
        private readonly RaycastHit[] directHits = new RaycastHit[32];
        private readonly RaycastHit[] sphereHits = new RaycastHit[32];
        private readonly List<WorldInteractionTargetCandidate> directCandidates =
            new List<WorldInteractionTargetCandidate>();
        private readonly List<WorldInteractionTargetCandidate> sphereCandidates =
            new List<WorldInteractionTargetCandidate>();
        private LocalPlayerController localPlayer;
        private WorldInteractionClientController interactionController;

        public event Action Changed;

        public IWorldInteractionTarget CurrentTarget { get; private set; }

        private void Start()
        {
            interactionController = ShooterMmoClientBootstrap.WorldInteractionController;
            FindLocalPlayer();
        }

        private void Update()
        {
            if (localPlayer == null || localPlayer.PlayerCamera == null)
            {
                FindLocalPlayer();
            }

            UpdateTarget();
            if (localPlayer?.PlayerInput == null
                || !localPlayer.PlayerInput.InteractPressedThisFrame
                || interactionController == null)
            {
                return;
            }

            if (interactionController.HasActiveLease)
            {
                SubmitCloseActive();
                return;
            }

            if (interactionController.State.PendingOperationId != Guid.Empty
                || CurrentTarget == null
                || !CurrentTarget.IsTargetActive)
            {
                return;
            }

            if (CurrentTarget.TargetKind
                == RealtimeWorldInteractionTargetKind.WorldActor)
            {
                var actors = ShooterMmoClientBootstrap.WorldActorController;
                if (actors != null
                    && actors.State.TryGet(CurrentTarget.TargetEntityId, out var actor))
                {
                    SubmitActorOpen(actor);
                }

                return;
            }

            if (!interactionController.TryOpenCorpse(
                    CurrentTarget.TargetRuntimeId,
                    out var error))
            {
                LogSubmissionFailure(error);
            }
        }

        public static IWorldInteractionTarget SelectCandidate(
            IReadOnlyList<WorldInteractionTargetCandidate> direct,
            IReadOnlyList<WorldInteractionTargetCandidate> spherecast)
        {
            var selected = ClosestActive(direct);
            return selected ?? ClosestActive(spherecast);
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

        private void UpdateTarget()
        {
            var camera = localPlayer?.PlayerCamera?.GetComponent<Camera>();
            if (camera == null)
            {
                SetCurrentTarget(null);
                return;
            }

            var ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            directCandidates.Clear();
            sphereCandidates.Clear();
            var directCount = Physics.RaycastNonAlloc(
                ray,
                directHits,
                DiscoveryRange,
                Physics.AllLayers,
                QueryTriggerInteraction.Collide);
            AddCandidates(directHits, directCount, directCandidates);
            if (directCandidates.Count == 0)
            {
                var sphereCount = Physics.SphereCastNonAlloc(
                    ray,
                    SpherecastRadius,
                    sphereHits,
                    DiscoveryRange,
                    Physics.AllLayers,
                    QueryTriggerInteraction.Collide);
                AddCandidates(sphereHits, sphereCount, sphereCandidates);
            }

            SetCurrentTarget(SelectCandidate(directCandidates, sphereCandidates));
        }

        private static void AddCandidates(
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

        private void SetCurrentTarget(IWorldInteractionTarget target)
        {
            if (ReferenceEquals(CurrentTarget, target))
            {
                return;
            }

            CurrentTarget = target;
            Changed?.Invoke();
        }

        private void FindLocalPlayer()
        {
            localPlayer = FindAnyObjectByType<LocalPlayerController>();
        }

        private void SubmitActorOpen(WorldActorClientEntry actor)
        {
            if (!interactionController.TryOpenActor(actor, out var error))
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

        private static void LogSubmissionFailure(string error)
        {
            ClientLog.Warning(
                ClientLogCategory.Client,
                "World interaction was not submitted: " + error);
        }
    }
}
