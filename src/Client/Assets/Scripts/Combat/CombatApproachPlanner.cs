using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Combat
{
    public enum CombatApproachAction
    {
        None,

        // Walk until the character is within Range of the target.
        Approach,

        // Stop the approach, because the target is gone.
        Stop
    }

    public readonly struct CombatApproachDecision
    {
        public CombatApproachDecision(CombatApproachAction action, float range)
        {
            Action = action;
            Range = range;
        }

        public CombatApproachAction Action { get; }

        public float Range { get; }
    }

    // What the planner sees each frame.
    public readonly struct CombatApproachInput
    {
        public CombatApproachInput(
            ulong targetEntityId,
            float? weaponRange,
            Vector3 position,
            Vector3? targetPosition,
            bool isMoving,
            bool ownsNavigation,
            RealtimeCombatAttackStatus? status,
            float time)
        {
            TargetEntityId = targetEntityId;
            WeaponRange = weaponRange;
            Position = position;
            TargetPosition = targetPosition;
            IsMoving = isMoving;
            OwnsNavigation = ownsNavigation;
            Status = status;
            Time = time;
        }

        // The attack target, or 0 without one.
        public ulong TargetEntityId { get; }

        // The range of the equipped weapon, or null without a weapon.
        public float? WeaponRange { get; }

        // The authoritative position of the character.
        public Vector3 Position { get; }

        // The presented position of the target, or null when it has no presentation.
        public Vector3? TargetPosition { get; }

        // Whether the character follows a path or waits for a planned one.
        public bool IsMoving { get; }

        // Whether the newest navigation request is the planner's approach.
        public bool OwnsNavigation { get; }

        // The worker's attack status while it attacks this target, otherwise null.
        public RealtimeCombatAttackStatus? Status { get; }

        public float Time { get; }
    }

    // Decides when the character walks toward its attack target. It approaches to the
    // weapon range minus a margin, walks again when the target moves out of range or the
    // worker reports it out of range, and walks closer each time the worker reports that
    // line of sight is blocked. The worker owns range and line of sight; this only chooses
    // where to walk. Without a weapon the character does not approach.
    public sealed class CombatApproachPlanner
    {
        // Keeps the approach inside the worker's range, which it measures to the target's
        // bounds and in three dimensions, while the route stops on the planar distance.
        public const float RangeMargin = 0.75f;
        public const float MinimumRange = 0.5f;

        // How much closer each blocked line of sight walks.
        public const float LineOfSightStep = 2f;

        // A moving target farther than this from the planned goal gets a new route.
        public const float ReplanDistance = 1.5f;

        // The shortest time between two approaches that the planner starts by itself.
        public const float RetrySeconds = 1f;

        private ulong targetEntityId;
        private bool requested;
        private float range;
        private Vector3 goal;
        private float requestedAt = float.NegativeInfinity;

        public ulong TargetEntityId
        {
            get { return targetEntityId; }
        }

        // The range of the newest approach.
        public float RequestedRange
        {
            get { return range; }
        }

        public static float ApproachRange(float weaponRange)
        {
            return Mathf.Max(Mathf.Min(weaponRange, MinimumRange), weaponRange - RangeMargin);
        }

        public static float PlanarDistance(Vector3 first, Vector3 second)
        {
            var deltaX = first.x - second.x;
            var deltaZ = first.z - second.z;
            return Mathf.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        }

        public CombatApproachDecision Update(CombatApproachInput input)
        {
            if (input.TargetEntityId != targetEntityId)
            {
                var wasApproaching = targetEntityId != 0 && requested;
                targetEntityId = input.TargetEntityId;
                requested = false;
                requestedAt = float.NegativeInfinity;
                if (targetEntityId == 0)
                {
                    return wasApproaching && input.OwnsNavigation && input.IsMoving
                        ? new CombatApproachDecision(CombatApproachAction.Stop, 0f)
                        : default(CombatApproachDecision);
                }
            }

            if (targetEntityId == 0
                || !input.WeaponRange.HasValue
                || !input.TargetPosition.HasValue)
            {
                return default(CombatApproachDecision);
            }

            var weaponRange = input.WeaponRange.Value;
            var preferred = ApproachRange(weaponRange);
            var target = input.TargetPosition.Value;

            // The first approach also stops a walk that is already in range.
            if (!requested)
            {
                return Request(preferred, target, input.Time);
            }

            var canRetry = input.Time - requestedAt >= RetrySeconds;
            if (input.IsMoving)
            {
                return input.OwnsNavigation
                    && canRetry
                    && PlanarDistance(goal, target) > ReplanDistance
                        ? Request(range, target, input.Time)
                        : default(CombatApproachDecision);
            }

            // The worker attacks, so its range and line of sight hold; never walk then.
            if (input.Status == RealtimeCombatAttackStatus.Attacking)
            {
                range = preferred;
                return default(CombatApproachDecision);
            }

            if (!canRetry)
            {
                return default(CombatApproachDecision);
            }

            var distance = PlanarDistance(input.Position, target);
            if (distance > weaponRange)
            {
                return Request(preferred, target, input.Time);
            }

            if (input.Status == RealtimeCombatAttackStatus.OutOfRange)
            {
                return Request(
                    Mathf.Max(MinimumRange, Mathf.Min(range, distance - RangeMargin)),
                    target,
                    input.Time);
            }

            if (input.Status == RealtimeCombatAttackStatus.LineOfSightBlocked)
            {
                return Request(
                    Mathf.Max(MinimumRange, Mathf.Min(range, distance) - LineOfSightStep),
                    target,
                    input.Time);
            }

            return default(CombatApproachDecision);
        }

        // Forgets the target, so the next update starts a fresh approach.
        public void Reset()
        {
            targetEntityId = 0;
            requested = false;
            range = 0f;
            requestedAt = float.NegativeInfinity;
        }

        private CombatApproachDecision Request(float approachRange, Vector3 target, float time)
        {
            requested = true;
            range = approachRange;
            goal = target;
            requestedAt = time;
            return new CombatApproachDecision(CombatApproachAction.Approach, approachRange);
        }
    }
}
