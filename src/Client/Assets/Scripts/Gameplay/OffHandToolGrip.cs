using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Pins the other hand of a body on a tool that both hands hold, such as a pickaxe in Mine: that
    // hand's socket goes to the point of the tool where the hand holds it, turned so both thumbs
    // point along the haft, and the arm bends to reach it with the elbow on the side the clip bends
    // it to. Clips are keyed on one body and retargeted to every other, so without it the off hand
    // of a body with other proportions would miss the haft. Runs after the Animator posed the body,
    // blended by how much of the pose comes from the tool's clip.
    public static class OffHandToolGrip
    {
        // The off hand's socket is turned half around the forward axis of the main hand's socket.
        public static readonly Quaternion OffHandTurn = Quaternion.AngleAxis(180f, Vector3.forward);

        // A haft just beyond the arm's reach brings the shoulder forward, as far as a collar bone
        // turns, a humanoid's shoulder muscle range.
        public const float MaximumShoulderDegrees = 15f;

        // Where the off hand's socket goes: the grip point of the tool in the main hand's socket space.
        public static void Target(Transform mainSocket, Vector3 offHandGrip, out Vector3 position, out Quaternion rotation)
        {
            position = mainSocket.TransformPoint(offHandGrip);
            rotation = mainSocket.rotation * OffHandTurn;
        }

        public static void Pin(
            Transform mainSocket,
            Vector3 offHandGrip,
            Transform shoulder,
            Transform upper,
            Transform lower,
            Transform hand,
            Transform offSocket,
            float weight)
        {
            if (weight <= 0f || mainSocket == null || upper == null || lower == null || hand == null || offSocket == null)
            {
                return;
            }

            Target(mainSocket, offHandGrip, out var targetPosition, out var targetRotation);
            var handRotation = targetRotation * Quaternion.Inverse(offSocket.localRotation);
            var wrist = targetPosition - handRotation * Vector3.Scale(hand.lossyScale, offSocket.localPosition);
            var shoulderFrom = shoulder != null ? shoulder.rotation : Quaternion.identity;
            var upperFrom = upper.rotation;
            var lowerFrom = lower.rotation;
            var handFrom = hand.rotation;
            if (shoulder != null)
            {
                Extend(shoulder, upper, lower, hand, wrist);
            }

            Reach(upper, lower, hand, wrist);
            if (weight >= 1f)
            {
                hand.rotation = handRotation;
                return;
            }

            var shoulderTo = shoulder != null ? shoulder.rotation : Quaternion.identity;
            var upperTo = upper.rotation;
            var lowerTo = lower.rotation;
            if (shoulder != null)
            {
                shoulder.rotation = Quaternion.Slerp(shoulderFrom, shoulderTo, weight);
            }

            upper.rotation = Quaternion.Slerp(upperFrom, upperTo, weight);
            lower.rotation = Quaternion.Slerp(lowerFrom, lowerTo, weight);
            hand.rotation = Quaternion.Slerp(handFrom, handRotation, weight);
        }

        // Turns the collar bone toward a target beyond the arm's reach, by the least angle that
        // brings it in reach and at most MaximumShoulderDegrees; a target in reach leaves it.
        public static void Extend(Transform shoulder, Transform upper, Transform lower, Transform hand, Vector3 target)
        {
            var reach = (lower.position - upper.position).magnitude + (hand.position - lower.position).magnitude - 1e-3f;
            var collar = upper.position - shoulder.position;
            var toTarget = target - shoulder.position;
            if ((target - upper.position).magnitude <= reach || collar.sqrMagnitude < 1e-10f || toTarget.sqrMagnitude < 1e-10f)
            {
                return;
            }

            var axis = Vector3.Cross(collar, toTarget);
            if (axis.sqrMagnitude < 1e-12f)
            {
                return;
            }

            // The farthest the collar may turn, then halving toward the least turn that reaches.
            axis.Normalize();
            var low = 0f;
            var high = Mathf.Min(MaximumShoulderDegrees, Vector3.Angle(collar, toTarget));
            if ((target - (shoulder.position + Quaternion.AngleAxis(high, axis) * collar)).magnitude > reach)
            {
                low = high;
            }

            for (var step = 0; step < 12 && high - low > 0.01f; step++)
            {
                var middle = (low + high) * 0.5f;
                if ((target - (shoulder.position + Quaternion.AngleAxis(middle, axis) * collar)).magnitude > reach)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            shoulder.rotation = Quaternion.AngleAxis(high, axis) * shoulder.rotation;
        }

        // Turns the upper and lower bone so the hand's root reaches the target, as close as the
        // limb's length allows, with the elbow kept in the plane it bends in now.
        public static void Reach(Transform upper, Transform lower, Transform hand, Vector3 target)
        {
            var shoulder = upper.position;
            var upperLength = (lower.position - shoulder).magnitude;
            var lowerLength = (hand.position - lower.position).magnitude;
            var toTarget = target - shoulder;
            if (upperLength < 1e-5f || lowerLength < 1e-5f || toTarget.sqrMagnitude < 1e-10f)
            {
                return;
            }

            var axis = toTarget.normalized;
            var distance = Mathf.Clamp(
                toTarget.magnitude,
                Mathf.Abs(upperLength - lowerLength) + 1e-4f,
                upperLength + lowerLength - 1e-4f);
            var bend = Vector3.ProjectOnPlane(lower.position - shoulder, axis);
            if (bend.sqrMagnitude < 1e-10f)
            {
                bend = Vector3.ProjectOnPlane(Vector3.down, axis);
            }

            var cosine = (upperLength * upperLength + distance * distance - lowerLength * lowerLength)
                / (2f * upperLength * distance);
            var angle = Mathf.Acos(Mathf.Clamp(cosine, -1f, 1f));
            var elbow = shoulder + axis * (upperLength * Mathf.Cos(angle)) + bend.normalized * (upperLength * Mathf.Sin(angle));
            upper.rotation = Quaternion.FromToRotation(lower.position - shoulder, elbow - shoulder) * upper.rotation;
            var reached = shoulder + axis * distance;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, reached - lower.position) * lower.rotation;
        }
    }
}
