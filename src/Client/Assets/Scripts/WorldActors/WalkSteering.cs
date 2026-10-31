using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    // Steers a walk while the left button stays down after a left click that walked: the
    // destination follows the ground under the pointer. A new route is planned at most ten
    // times a second and only when the point moved, so holding costs no more than clicking
    // quickly. Another feature that replaces the walk, such as an attack, a death, or an
    // interaction, ends the steering.
    public sealed class WalkSteering
    {
        // Seconds between two planned routes.
        public const float RouteInterval = 0.1f;

        // Meters the point must move before a new route is planned.
        public const float MinimumShift = 0.25f;

        private int intentId;
        private Vector3 destination;
        private float nextRouteTime;

        public bool IsSteering
        {
            get { return intentId != 0; }
        }

        // Steers the walk with this navigation intent and destination from now on. An intent
        // of zero, which means no walk started, steers nothing.
        public void Follow(int walkIntentId, Vector3 walkDestination, float time)
        {
            intentId = walkIntentId;
            destination = walkDestination;
            nextRouteTime = time + RouteInterval;
        }

        public void End()
        {
            intentId = 0;
        }

        // True when a route to the point should be planned now. A navigation intent other
        // than the steered one means another feature took over, which ends the steering.
        public bool ShouldSteerTo(Vector3 point, int currentIntentId, float time)
        {
            if (intentId == 0)
            {
                return false;
            }

            if (currentIntentId != intentId)
            {
                End();
                return false;
            }

            return time >= nextRouteTime
                && (point - destination).sqrMagnitude >= MinimumShift * MinimumShift;
        }
    }
}
