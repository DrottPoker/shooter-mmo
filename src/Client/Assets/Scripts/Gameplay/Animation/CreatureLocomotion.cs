using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // How a creature with clips of its own animates, such as the wolf of art/Creatures/Wolf. Its
    // Walk and Run clips are made for the speeds of its asset at the model's own size, so its paws
    // stay planted when a clip plays at its speed: the wolf walks at 1.4 m/s, the patrol speed of
    // its locomotion profile, and gallops at 4.5 m/s, its chase speed. A larger creature of the
    // same model, such as the Feral Alpha at 1.3 times the size, covers that much more ground with
    // each stride, so its clips play slower at the same speed. Between the gaits the Animator blends
    // the two clips, and the playback rate is set so the blended stride matches the speed.
    public static class CreatureLocomotion
    {
        // Below this speed the creature stands in Idle.
        public const float MovingSpeed = 0.1f;

        // The walk never plays slower than this, so a creature that slows to a stop does not move
        // in slow motion while it blends to Idle.
        public const float MinimumRate = 0.3f;

        public const string SpeedParameter = "Speed";
        public const string MovingParameter = "Moving";
        public const string RateParameter = "LocomotionRate";
        public const string AttackParameter = "Attack";
        public const string DeadParameter = "Dead";
        public const string IdleState = "Idle";
        public const string LocomotionState = "Locomotion";
        public const string AttackState = "Attack";
        public const string DeathState = "Death";
        public const string IdleClip = "Idle";
        public const string WalkClip = "Walk";
        public const string RunClip = "Run";
        public const string AttackClip = "Attack";
        public const string DeathClip = "Death";
        public const float DeathCrossfadeSeconds = 0.15f;
        public const float AttackCrossfadeSeconds = 0.08f;

        // The blend parameter and playback rate at a ground speed, from the speeds and cycle
        // lengths of the Walk and Run clips at the creature's size. The blend parameter is in the
        // clips' own speeds, as the blend tree's thresholds are. A blended cycle lasts the weighted
        // mean of both lengths, as the Animator synchronizes them, and covers the weighted mean of
        // both strides.
        public static void Evaluate(
            float speed,
            float scale,
            float walkSpeed,
            float runSpeed,
            float walkCycleSeconds,
            float runCycleSeconds,
            out float blend,
            out float rate)
        {
            speed = Mathf.Max(speed, 0f);
            scale = scale > 0f ? scale : 1f;
            var walk = walkSpeed * scale;
            var run = runSpeed * scale;
            if (speed <= walk)
            {
                blend = walkSpeed;
                rate = Mathf.Max(speed / walk, MinimumRate);
                return;
            }

            var weight = Mathf.Clamp01((speed - walk) / (run - walk));
            blend = Mathf.Lerp(walkSpeed, runSpeed, weight);
            var cycle = Mathf.Lerp(walkCycleSeconds, runCycleSeconds, weight);
            var stride = Mathf.Lerp(walk * walkCycleSeconds, run * runCycleSeconds, weight);
            rate = stride > 0f ? speed * cycle / stride : 1f;
        }
    }
}
