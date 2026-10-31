using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // How the human body animates at a ground speed. Every clip comes from the Human_Actions
    // animation library. The Walk and Jog clips are authored for these speeds, which the gait
    // model tools/art/blender/human_base/animate.py starts them at (WALK_SPEED and JOG_SPEED), so
    // their feet stay planted when the clip plays at its speed. Between them the Animator blends the
    // two clips, and the playback rate is set so the blended stride matches the speed. Below
    // the walk speed, as when a character is encumbered, the walk plays slower. A dead body
    // plays Death instead.
    public static class HumanLocomotion
    {
        public const float WalkSpeed = 1.4f;
        public const float JogSpeed = 5.0f;

        // Below this speed the body stands in Idle.
        public const float MovingSpeed = 0.1f;

        // The walk never plays slower than this, so a character that slows down to a stop
        // does not move in slow motion while it blends to Idle.
        public const float MinimumRate = 0.3f;

        public const string SpeedParameter = "Speed";
        public const string MovingParameter = "Moving";
        public const string RateParameter = "LocomotionRate";
        public const string IdleState = "Idle";
        public const string LocomotionState = "Locomotion";
        public const string IdleClip = "Idle";
        public const string WalkClip = "Walk";
        public const string JogClip = "Jog";

        // Any state crossfades into Death while Dead is true, and it holds its last pose, lying
        // on the ground.
        public const string DeadParameter = "Dead";
        public const string DeathState = "Death";
        public const string DeathClip = "Death";
        public const float DeathCrossfadeSeconds = 0.15f;

        // While a socket in a hand holds an attachment, such as a sword, the fingers of that hand
        // close around its grip: the clip Grip plays in the state Grip on a layer of that hand's
        // fingers only, at full weight, over every other clip.
        public const string GripClip = "Grip";
        public const string GripState = "Grip";
        public const string MainHandGripLayer = "MainHandGrip";
        public const string OffHandGripLayer = "OffHandGrip";
        public const string MainHandSocketBone = "Socket_MainHand";
        public const string OffHandSocketBone = "Socket_OffHand";

        // While the body holds a tool and stands, it plays the tool's clip, such as Mine or Chop,
        // in a looping base layer state of the clip's name, crossfading in and out over this time.
        public const string MineClip = "Mine";
        public const string ChopClip = "Chop";
        public const float UseCrossfadeSeconds = 0.2f;

        // While a hand holds a weapon that names a hold clip, such as a staff in the main hand or a
        // bow in the off hand, that arm holds the clip's pose: the clip plays in the state of its
        // name on a layer of that hand's arm only, at full weight, over the locomotion clips. A layer
        // rests in its empty state.
        public const string MainHandHoldLayer = "MainHandHold";
        public const string OffHandHoldLayer = "OffHandHold";
        public const string HoldStaffClip = "HoldStaff";
        public const string EmptyState = "Empty";

        // Whenever a weapon's attack starts, the body plays the weapon's attack clip once, such as
        // Cast, in the state of its name on a layer of the whole body without the fingers, over
        // every other clip, fading in and out over these times; its feet step with it. It starts so
        // that the moment its blow lands or its arrow or bolt leaves comes when the server releases
        // the attack. The clip starts and ends in the hold pose, so the fades only soften a cut. It
        // ends early when the body starts to move or the attack is cancelled.
        public const string ActionLayer = "Action";
        public const string CastClip = "Cast";
        public const float ActionFadeInSeconds = 0.06f;
        public const float ActionFadeOutSeconds = 0.15f;

        // A melee weapon in one hand without an attack clip of its own strikes with this clip, whose
        // blow lands this many seconds in.
        public const string OneHandStrikeClip = "OneHandStrike";
        public const float OneHandStrikeReleaseSeconds = 0.4f;

        // The weight of an attack clip this many seconds after its start that lasts this long and
        // shows from shownFrom seconds in, such as one that began late.
        public static float ActionWeight(float elapsed, float length, float shownFrom = 0f)
        {
            if (elapsed < shownFrom || elapsed >= length)
            {
                return 0f;
            }

            var fadeIn = Mathf.Clamp01((elapsed - shownFrom) / ActionFadeInSeconds);
            var fadeOut = Mathf.Clamp01((length - elapsed) / ActionFadeOutSeconds);
            return Mathf.Min(fadeIn, fadeOut);
        }

        // The seconds into an attack clip to start it at, so the moment it releases, release seconds
        // in, comes when the attack releases, secondsUntilRelease from now: negative to start it that
        // much later, and never past the release, so a late start still shows the blow land.
        public static float AttackClipTime(float release, float secondsUntilRelease)
        {
            return release - Mathf.Max(secondsUntilRelease, 0f);
        }

        // The grip layer of a socket bone, or null for a socket that is not in a hand.
        public static string GripLayerFor(string socketBone)
        {
            switch (socketBone)
            {
                case MainHandSocketBone:
                    return MainHandGripLayer;
                case OffHandSocketBone:
                    return OffHandGripLayer;
                default:
                    return null;
            }
        }

        public enum LifeChange
        {
            None,
            // Dies in view: the body falls.
            Fall,
            // Was dead before the body showed a life state, such as a player who died before
            // coming into view: the body lies in the end pose at once.
            LieDead,
            // Lives again: the body stands in Idle at once, as a respawn teleports it.
            StandUp,
        }

        // How a body shows a new life state, from the one it shows now.
        public static LifeChange LifeChangeFor(bool hasLifeState, bool shownDead, bool dead)
        {
            if (!hasLifeState)
            {
                return dead ? LifeChange.LieDead : LifeChange.None;
            }

            if (shownDead == dead)
            {
                return LifeChange.None;
            }

            return dead ? LifeChange.Fall : LifeChange.StandUp;
        }

        // The blend parameter and playback rate at a ground speed, from the lengths of the
        // Walk and Jog cycles. A blended cycle lasts the weighted mean of both lengths, as the
        // Animator synchronizes them, and covers the weighted mean of both strides.
        public static void Evaluate(float speed, float walkCycleSeconds, float jogCycleSeconds, out float blend, out float rate)
        {
            speed = Mathf.Max(speed, 0f);
            if (speed <= WalkSpeed)
            {
                blend = WalkSpeed;
                rate = Mathf.Max(speed / WalkSpeed, MinimumRate);
                return;
            }

            blend = Mathf.Min(speed, JogSpeed);
            var weight = (blend - WalkSpeed) / (JogSpeed - WalkSpeed);
            var cycle = Mathf.Lerp(walkCycleSeconds, jogCycleSeconds, weight);
            var stride = Mathf.Lerp(WalkSpeed * walkCycleSeconds, JogSpeed * jogCycleSeconds, weight);
            rate = stride > 0f ? speed * cycle / stride : 1f;
        }
    }
}
