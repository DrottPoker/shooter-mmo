using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Drives the locomotion parameters of a character body's Animator from how fast the body
    // moved over the ground this frame, so the local player, remote players, and anything else
    // that moves a body animate the same way without knowing about each other. The speed is
    // smoothed over a short time, and a jump farther than a character can run, such as a
    // teleport or a correction, is not taken as movement. Combat tells the body whether its
    // character is dead, and a dead body plays Death and stays lying.
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
        private static readonly int DeadId = Animator.StringToHash(HumanLocomotion.DeadParameter);
        private static readonly int DeathStateId = Animator.StringToHash(HumanLocomotion.DeathState);
        private static readonly int IdleStateId = Animator.StringToHash(HumanLocomotion.IdleState);

        private Animator animator;
        private float walkCycleSeconds = 1f;
        private float jogCycleSeconds = 1f;
        private Vector3 previousPosition;
        private bool hasPreviousPosition;
        private int fallFrame = -1;

        // The smoothed ground speed in meters per second.
        public float Speed { get; private set; }

        // Whether the body was told a life state since it appeared, and whether it shows its
        // character dead.
        public bool HasLifeState { get; private set; }

        public bool IsDead { get; private set; }

        // Shows the character's life state: a fall into Death when it dies in view, the end
        // pose of Death at once when the body first shows it dead, and Idle at once when it
        // lives again.
        public void ShowLifeState(bool dead)
        {
            var change = HumanLocomotion.LifeChangeFor(HasLifeState, IsDead, dead);
            HasLifeState = true;
            IsDead = dead;
            if (change == HumanLocomotion.LifeChange.None || !HasController)
            {
                return;
            }

            animator.SetBool(DeadId, dead);
            switch (change)
            {
                case HumanLocomotion.LifeChange.Fall:
                    fallFrame = Time.frameCount;
                    break;
                case HumanLocomotion.LifeChange.LieDead:
                    animator.Play(DeathStateId, 0, 1f);
                    break;
                case HumanLocomotion.LifeChange.StandUp:
                    animator.Play(IdleStateId, 0, 0f);
                    break;
            }
        }

        private bool HasController
        {
            get { return animator != null && animator.runtimeAnimatorController != null; }
        }

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
            if (!HasController)
            {
                return;
            }

            HumanLocomotion.Evaluate(Speed, walkCycleSeconds, jogCycleSeconds, out var blend, out var rate);
            animator.SetBool(MovingId, Speed >= HumanLocomotion.MovingSpeed);
            animator.SetFloat(SpeedId, blend);
            animator.SetFloat(RateId, rate);
            animator.SetBool(DeadId, IsDead);

            // An Animator that was rebound or enabled again starts over in Idle; a dead body
            // goes back to lying. A fall that just began needs a frame to enter Death.
            if (IsDead && Time.frameCount > fallFrame + 1 && !InOrEnteringDeath())
            {
                animator.Play(DeathStateId, 0, 1f);
            }
        }

        private bool InOrEnteringDeath()
        {
            return animator.GetCurrentAnimatorStateInfo(0).shortNameHash == DeathStateId
                || (animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).shortNameHash == DeathStateId);
        }
    }
}
