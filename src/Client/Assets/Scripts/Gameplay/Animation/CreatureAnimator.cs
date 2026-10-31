using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Plays a creature's own clips on its Animator, after CreatureLocomotion: Idle, Walk, and Run by
    // the speed it moves at in the world, Attack once whenever it attacks, and Death when it dies,
    // holding the last pose. As a corpse it lies in that last pose, or plays on a fall that began on
    // the living creature, so the corpse takes over without a jump.
    [DisallowMultipleComponent]
    public sealed class CreatureAnimator : MonoBehaviour
    {
        // A creature that moves faster than this between two frames was placed, not moved.
        private const float TeleportSpeed = 20f;
        private const float SmoothingSeconds = 0.12f;

        private static readonly int SpeedId = Animator.StringToHash(CreatureLocomotion.SpeedParameter);
        private static readonly int MovingId = Animator.StringToHash(CreatureLocomotion.MovingParameter);
        private static readonly int RateId = Animator.StringToHash(CreatureLocomotion.RateParameter);
        private static readonly int AttackId = Animator.StringToHash(CreatureLocomotion.AttackParameter);
        private static readonly int DeadId = Animator.StringToHash(CreatureLocomotion.DeadParameter);
        private static readonly int DeathStateId = Animator.StringToHash(CreatureLocomotion.DeathState);

        [SerializeField]
        private Animator animator;

        // The speeds the Walk and Run clips are made for, in meters per second at the model's own
        // size: those of the creature's art asset.
        [SerializeField, Min(0.1f)]
        private float walkSpeed = 1.4f;

        [SerializeField, Min(0.1f)]
        private float runSpeed = 4.5f;

        private bool hasPreviousPosition;
        private Vector3 previousPosition;
        private float walkCycleSeconds;
        private float runCycleSeconds;
        private float deathLength;
        private float deathStartTime = -1f;

        public Animator Animator
        {
            get { return animator; }
        }

        public float WalkSpeed
        {
            get { return walkSpeed; }
        }

        public float RunSpeed
        {
            get { return runSpeed; }
        }

        // The smoothed ground speed the creature moves at, in meters per second.
        public float Speed { get; private set; }

        public bool IsDead { get; private set; }

        // How far into Death the creature is, in seconds; negative while it lives.
        public float SecondsIntoDeath
        {
            get { return IsDead && deathStartTime >= 0f ? Time.time - deathStartTime : -1f; }
        }

        private bool HasController
        {
            get { return animator != null && animator.runtimeAnimatorController != null; }
        }

        // Binds the Animator, as a prefab does in its own fields; for code that builds a creature.
        public void Bind(Animator target, float walk, float run)
        {
            animator = target;
            walkSpeed = walk;
            runSpeed = run;
            ReadClips();
        }

        private void Awake()
        {
            ReadClips();
        }

        private void OnEnable()
        {
            hasPreviousPosition = false;
            Speed = 0f;
        }

        private void ReadClips()
        {
            if (!HasController)
            {
                return;
            }

            foreach (var clip in animator.runtimeAnimatorController.animationClips)
            {
                switch (clip.name)
                {
                    case CreatureLocomotion.WalkClip:
                        walkCycleSeconds = clip.length;
                        break;
                    case CreatureLocomotion.RunClip:
                        runCycleSeconds = clip.length;
                        break;
                    case CreatureLocomotion.DeathClip:
                        deathLength = clip.length;
                        break;
                }
            }
        }

        // The creature attacks: Attack plays once over whatever it does. A dead creature does not.
        public void PlayAttack()
        {
            if (HasController && !IsDead)
            {
                animator.SetTrigger(AttackId);
            }
        }

        // The creature dies in view and falls, or lives again. Repeating the state changes nothing.
        public void ShowLifeState(bool dead)
        {
            if (dead == IsDead)
            {
                return;
            }

            IsDead = dead;
            deathStartTime = dead ? Time.time : -1f;
            if (!HasController)
            {
                return;
            }

            animator.ResetTrigger(AttackId);
            animator.SetBool(DeadId, dead);
            if (dead)
            {
                animator.CrossFadeInFixedTime(DeathStateId, CreatureLocomotion.DeathCrossfadeSeconds, 0);
            }
        }

        // The creature lies dead as a corpse: in Death that many seconds in, such as the rest of a
        // fall that began on the living creature, or in its last pose.
        public void LieDead(float secondsIntoDeath)
        {
            IsDead = true;
            Speed = 0f;
            if (!HasController)
            {
                return;
            }

            var normalized = deathLength > 0f && secondsIntoDeath >= 0f
                ? Mathf.Clamp01(secondsIntoDeath / deathLength)
                : 1f;
            deathStartTime = Time.time - normalized * deathLength;
            animator.SetBool(DeadId, true);
            animator.Play(DeathStateId, 0, normalized);
            // The pose is written at once, though no camera has seen the new corpse yet.
            var culling = animator.cullingMode;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Update(0f);
            animator.cullingMode = culling;
        }

        private void Update()
        {
            var position = transform.position;
            var deltaTime = Time.deltaTime;
            if (hasPreviousPosition && deltaTime > 0f && !IsDead)
            {
                var moved = position - previousPosition;
                moved.y = 0f;
                var measured = moved.magnitude / deltaTime;
                if (measured < TeleportSpeed)
                {
                    Speed = Mathf.Lerp(Speed, measured, 1f - Mathf.Exp(-deltaTime / SmoothingSeconds));
                }
            }

            previousPosition = position;
            hasPreviousPosition = true;
            if (!HasController)
            {
                return;
            }

            // The model may be larger than its asset, such as the Feral Alpha's wolf; the root keeps
            // the actor's own scale, so its interaction bounds stay in meters.
            CreatureLocomotion.Evaluate(
                Speed,
                animator.transform.lossyScale.y,
                walkSpeed,
                runSpeed,
                walkCycleSeconds,
                runCycleSeconds,
                out var blend,
                out var rate);
            animator.SetBool(MovingId, !IsDead && Speed >= CreatureLocomotion.MovingSpeed);
            animator.SetFloat(SpeedId, blend);
            animator.SetFloat(RateId, rate);
            animator.SetBool(DeadId, IsDead);
        }
    }
}
