using System.Collections.Generic;
using Mmorpg.Client.Gameplay;
using Mmorpg.WorldData.Actors;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    // Something the pointer ray hit and its distance along the ray: a registered world
    // target, such as an NPC, a Mob, a resource node, or a corpse, or another player.
    public readonly struct WorldPointerCandidate
    {
        public WorldPointerCandidate(IWorldInteractionTarget target, float distance)
        {
            Target = target;
            Player = null;
            Distance = distance;
        }

        public WorldPointerCandidate(RemotePlayerView player, float distance)
        {
            Target = null;
            Player = player;
            Distance = distance;
        }

        public IWorldInteractionTarget Target { get; }

        public RemotePlayerView Player { get; }

        public float Distance { get; }

        public bool IsEmpty
        {
            get { return Target == null && Player == null; }
        }

        public bool IsActive
        {
            get
            {
                if (Target != null)
                {
                    return Target.IsTargetActive
                        && !(Target is Object unityObject && unityObject == null);
                }

                return Player != null && Player.gameObject.activeInHierarchy;
            }
        }
    }

    // Picks what the pointer points at, the same way for every feature: a direct ray first,
    // then a small spherecast tolerance, among registered targets within the pointer range
    // that no solid collider hides. The nearest active candidate wins, so a feature that
    // does not handle it never reaches past it to a target behind.
    public sealed class WorldPointerPicker
    {
        public const float PointerRange = WorldInteractionRules.ClientPointerRange;
        public const float SpherecastRadius = WorldInteractionRules.ClientSpherecastRadius;

        private readonly RaycastHit[] directHits = new RaycastHit[32];
        private readonly RaycastHit[] sphereHits = new RaycastHit[32];
        private readonly List<WorldPointerCandidate> directCandidates =
            new List<WorldPointerCandidate>();
        private readonly List<WorldPointerCandidate> sphereCandidates =
            new List<WorldPointerCandidate>();

        // The nearest candidate under the pointer, or an empty one. Colliders below the
        // ignored root, the local player, neither count nor hide anything.
        public WorldPointerCandidate Pick(Ray ray, Transform ignoredRoot)
        {
            directCandidates.Clear();
            sphereCandidates.Clear();
            var directCount = Physics.RaycastNonAlloc(
                ray,
                directHits,
                PointerRange,
                Physics.AllLayers,
                QueryTriggerInteraction.Collide);
            AddCandidates(directHits, directCount, ignoredRoot, directCandidates);
            if (directCandidates.Count == 0)
            {
                var sphereCount = Physics.SphereCastNonAlloc(
                    ray,
                    SpherecastRadius,
                    sphereHits,
                    PointerRange,
                    Physics.AllLayers,
                    QueryTriggerInteraction.Collide);
                AddCandidates(sphereHits, sphereCount, ignoredRoot, sphereCandidates);
            }

            return SelectCandidate(directCandidates, sphereCandidates);
        }

        // The nearest active direct candidate, else the nearest active spherecast candidate.
        public static WorldPointerCandidate SelectCandidate(
            IReadOnlyList<WorldPointerCandidate> direct,
            IReadOnlyList<WorldPointerCandidate> spherecast)
        {
            var selected = ClosestActive(direct);
            return selected.IsEmpty ? ClosestActive(spherecast) : selected;
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

        public static WorldPointerCandidate FindCandidate(Collider collider, float distance)
        {
            var target = FindRegisteredTarget(collider);
            if (target != null)
            {
                return new WorldPointerCandidate(target, distance);
            }

            var player = collider != null
                ? collider.GetComponentInParent<RemotePlayerView>(true)
                : null;
            return player != null
                ? new WorldPointerCandidate(player, distance)
                : default(WorldPointerCandidate);
        }

        private static WorldPointerCandidate ClosestActive(
            IReadOnlyList<WorldPointerCandidate> values)
        {
            var selected = default(WorldPointerCandidate);
            var selectedDistance = float.PositiveInfinity;
            for (var index = 0; index < values.Count; index++)
            {
                var candidate = values[index];
                if (candidate.IsEmpty
                    || !candidate.IsActive
                    || candidate.Distance < 0f
                    || candidate.Distance >= selectedDistance)
                {
                    continue;
                }

                selected = candidate;
                selectedDistance = candidate.Distance;
            }

            return selected;
        }

        // A candidate counts only when no solid collider stands between it and the camera.
        private static void AddCandidates(
            RaycastHit[] hits,
            int count,
            Transform ignoredRoot,
            ICollection<WorldPointerCandidate> destination)
        {
            var nearestBlockerDistance = float.PositiveInfinity;
            for (var index = 0; index < count; index++)
            {
                var collider = hits[index].collider;
                if (collider != null
                    && !collider.isTrigger
                    && !IsIgnored(collider, ignoredRoot)
                    && FindCandidate(collider, 0f).IsEmpty)
                {
                    nearestBlockerDistance = Mathf.Min(
                        nearestBlockerDistance,
                        hits[index].distance);
                }
            }

            for (var index = 0; index < count; index++)
            {
                var collider = hits[index].collider;
                if (IsIgnored(collider, ignoredRoot)
                    || hits[index].distance > nearestBlockerDistance + 0.001f)
                {
                    continue;
                }

                var candidate = FindCandidate(collider, hits[index].distance);
                if (!candidate.IsEmpty)
                {
                    destination.Add(candidate);
                }
            }
        }

        private static bool IsIgnored(Collider collider, Transform ignoredRoot)
        {
            return collider == null
                || (ignoredRoot != null && collider.transform.IsChildOf(ignoredRoot));
        }
    }
}
