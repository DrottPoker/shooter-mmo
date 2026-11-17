using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The shadows of the other players' bodies by their distance from the camera that renders the
    // World: a body beyond FarMeters casts no shadow until it comes within NearMeters again, so a
    // body at the boundary does not flicker. Every body checks its distance ChecksPerSecond times a
    // second at a time of its own, so the checks spread over the frames. The local player is no
    // other player and always casts its shadow. See docs/UNITY_CLIENT_ARCHITECTURE.md, Crowd
    // Performance.
    public sealed class FarBodies
    {
        // A body within NearMeters casts its shadow, and one beyond FarMeters does not.
        public const float NearMeters = 40f;
        public const float FarMeters = 44f;

        public const float ChecksPerSecond = 4f;

        private Vector3 eye;

        // Whether the camera is known; until it is every body casts its shadow.
        public bool HasEye { get; private set; }

        public Vector3 Eye
        {
            get { return eye; }
        }

        // The time of the frame, which every body's check reads.
        public float Now { get; private set; }

        // Where the camera that renders the World is, and the time, once a frame.
        public void SetEye(Vector3 position, float now)
        {
            eye = position;
            Now = now;
            HasEye = true;
        }

        public void ForgetEye()
        {
            HasEye = false;
        }

        // Whether a body at the distance that casts its shadow now, or not, casts one.
        public static bool CastsShadowAt(float distance, bool casts)
        {
            return distance < NearMeters || (casts && distance < FarMeters);
        }

        // The seconds into its check period at which a body checks, spread over the bodies by their
        // entity ids.
        public static float CheckOffsetOf(ulong entityId)
        {
            return (entityId % 16UL) / (16f * ChecksPerSecond);
        }
    }
}
