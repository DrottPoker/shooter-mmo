using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Drives the locomotion parameters of a character body's Animator from how fast the body
    // moved over the ground this frame, so the local player, remote players, and anything else
    // that moves a body animate the same way without knowing about each other. The speed is
    // smoothed over a short time, and a jump farther than a character can run, such as a
    // teleport or a correction, is not taken as movement.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
    [DefaultExecutionOrder(PresentationExecutionOrder.Locomotion)]
    public sealed class CharacterLocomotionAnimator : MonoBehaviour
    {
        // Short, so a character that stops does not keep walking on the spot.
        public const float SmoothingSeconds = 0.08f;
        public const float TeleportSpeed = 20f;

        private static readonly int SpeedId = Animator.StringToHash(HumanLocomotion.SpeedParameter);
        private static readonly int MovingId = Animator.StringToHash(HumanLocomotion.MovingParameter);
        private static readonly int RateId = Animator.StringToHash(HumanLocomotion.RateParameter);

        private Animator animator;
        private float walkCycleSeconds = 1f;
        private float jogCycleSeconds = 1f;
        private Vector3 previousPosition;
        private bool hasPreviousPosition;

        // The smoothed ground speed in meters per second.
        public float Speed { get; private set; }

        private void Awake()
        {
            animator = GetComponent<Animator>();
            var controller = animator.runtimeAnimatorController;
            if (controller != null)
            {
                foreach (var clip in controller.animationClips)
                {
                    if (clip.name == HumanLocomotion.WalkClip)
                    {
                        walkCycleSeconds = clip.length;
                    }
                    else if (clip.name == HumanLocomotion.JogClip)
                    {
                        jogCycleSeconds = clip.length;
                    }
                }
            }
        }

        private void OnEnable()
        {
            hasPreviousPosition = false;
            Speed = 0f;
        }

        private void Update()
        {
            var position = transform.position;
            var deltaTime = Time.deltaTime;
            if (hasPreviousPosition && deltaTime > 0f)
            {
                var moved = position - previousPosition;
                moved.y = 0f;
                var measured = moved.magnitude / deltaTime;
                if (measured < TeleportSpeed)
                {
                    Speed = Smooth(Speed, measured, deltaTime);
                }
            }

            previousPosition = position;
            hasPreviousPosition = true;
            Apply();
        }

        public static float Smooth(float current, float measured, float deltaTime)
        {
            return Mathf.Lerp(current, measured, 1f - Mathf.Exp(-deltaTime / SmoothingSeconds));
        }

        private void Apply()
        {
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                return;
            }

            HumanLocomotion.Evaluate(Speed, walkCycleSeconds, jogCycleSeconds, out var blend, out var rate);
            animator.SetBool(MovingId, Speed >= HumanLocomotion.MovingSpeed);
            animator.SetFloat(SpeedId, blend);
            animator.SetFloat(RateId, rate);
        }
    }
}
