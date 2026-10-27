using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Drives the locomotion parameters of a character body's Animator from how fast the body
    // moved over the ground this frame, so the local player, remote players, and anything else
    // that moves a body animate the same way without knowing about each other. The speed is
    // smoothed over a short time, and a jump farther than a character can run, such as a
    // teleport or a correction, is not taken as movement. Combat tells the body whether its
    // character is dead, and a dead body plays Death and stays lying. While the body holds a tool,
    // such as a pickaxe while its character gathers, it plays the tool's clip, such as Mine,
    // whenever it stands and lives. While it holds a weapon with a hold clip, such as a staff, the
    // main hand's arm keeps that pose, and when the weapon's attack releases the upper body plays
    // the weapon's attack clip once, such as Cast.
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
        private static readonly int LocomotionStateId = Animator.StringToHash(HumanLocomotion.LocomotionState);

        private Animator animator;
        private float walkCycleSeconds = 1f;
        private float jogCycleSeconds = 1f;
        private Vector3 previousPosition;
        private bool hasPreviousPosition;
        private int fallFrame = -1;
        // The state of the tool's clip the body should play, and the one it played last, which it
        // leaves with a crossfade.
        private int useStateId;
        // Its full path in the base layer, which HasState takes.
        private int usePathId;
        private int shownUseStateId;
        private int holdLayer = -1;
        private int actionLayer = -1;
        private int holdStateId;
        // Whether the hold layer has a state of the hold clip's name, once the Animator could say.
        private bool? holdStateExists;
        private bool holdShown;
        private float actionStartedAt = -1f;
        private float actionLength;

        // The clip of the tool the body holds, such as Mine, or empty.
        public string UseClip { get; private set; } = string.Empty;

        // The pose the main hand's arm holds its weapon in, such as HoldStaff, or empty.
        public string HoldClip { get; private set; } = string.Empty;

        // How much of the upper body's pose comes from an attack clip that plays, from 0 to 1.
        public float ActionWeight { get; private set; }

        // Holds the main hand's arm in a weapon's pose, such as HoldStaff, or lets it move with the
        // other clips again for an empty clip.
        public void Hold(string clip)
        {
            clip = clip ?? string.Empty;
            if (string.Equals(clip, HoldClip, System.StringComparison.Ordinal))
            {
                return;
            }

            HoldClip = clip;
            holdStateId = clip.Length > 0 ? Animator.StringToHash(clip) : 0;
            holdStateExists = null;
        }

        // Plays a weapon's attack clip once over the upper body from its start, such as Cast; false
        // when the body lies dead or its controller has no state of that name on the action layer.
        public bool PlayAttack(string clip)
        {
            if (string.IsNullOrEmpty(clip) || IsDead || !HasController || actionLayer < 0
                || !animator.HasState(actionLayer, Animator.StringToHash(HumanLocomotion.ActionLayer + "." + clip)))
            {
                return false;
            }

            var length = ClipLength(clip);
            if (length <= 0f)
            {
                return false;
            }

            animator.Play(Animator.StringToHash(clip), actionLayer, 0f);
            actionStartedAt = Time.time;
            actionLength = length;
            return true;
        }

        // Plays the clip of a tool the body holds, in the base layer's state of the same name, or
        // stops playing one for an empty clip.
        public void Use(string clip)
        {
            UseClip = clip ?? string.Empty;
            useStateId = UseClip.Length > 0 ? Animator.StringToHash(UseClip) : 0;
            usePathId = 0;
        }

        // How much of the pose comes from the tool's clip, from 0 to 1, as the base layer crossfades
        // into or out of its state.
        public float UseWeight
        {
            get
            {
                if (shownUseStateId == 0 || !HasController || !animator.isInitialized)
                {
                    return 0f;
                }

                var current = animator.GetCurrentAnimatorStateInfo(0).shortNameHash == shownUseStateId;
                if (!animator.IsInTransition(0))
                {
                    return current ? 1f : 0f;
                }

                var next = animator.GetNextAnimatorStateInfo(0).shortNameHash == shownUseStateId;
                var progress = Mathf.Clamp01(animator.GetAnimatorTransitionInfo(0).normalizedTime);
                return current && next ? 1f : next ? progress : current ? 1f - progress : 0f;
            }
        }

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
                holdLayer = animator.GetLayerIndex(HumanLocomotion.MainHandHoldLayer);
                actionLayer = animator.GetLayerIndex(HumanLocomotion.ActionLayer);
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

        private float ClipLength(string clip)
        {
            foreach (var candidate in animator.runtimeAnimatorController.animationClips)
            {
                if (candidate.name == clip)
                {
                    return candidate.length;
                }
            }

            return 0f;
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

            ApplyUse();
            ApplyHold();
            ApplyAction();
        }

        // The main hand's arm holds the weapon's pose while the body lives and uses no tool. Bodies
        // that hold no such weapon set the layer only when they let go of one.
        private void ApplyHold()
        {
            if (holdLayer < 0)
            {
                return;
            }

            if (holdStateId != 0 && holdStateExists == null && animator.isInitialized)
            {
                holdStateExists = animator.HasState(
                    holdLayer,
                    Animator.StringToHash(HumanLocomotion.MainHandHoldLayer + "." + HoldClip));
            }

            var holds = holdStateId != 0 && holdStateExists == true && !IsDead && UseClip.Length == 0;
            if (!holds)
            {
                if (holdShown)
                {
                    animator.SetLayerWeight(holdLayer, 0f);
                    holdShown = false;
                }

                return;
            }

            // A rebound Animator starts its layers over, so the state and weight are set every frame.
            if (animator.GetCurrentAnimatorStateInfo(holdLayer).shortNameHash != holdStateId)
            {
                animator.Play(holdStateId, holdLayer, 0f);
            }

            animator.SetLayerWeight(holdLayer, 1f);
            holdShown = true;
        }

        // An attack clip fades in, plays once, and fades out; a body that dies or starts to move
        // ends it at once with a short fade.
        private void ApplyAction()
        {
            if (actionLayer < 0 || actionStartedAt < 0f)
            {
                return;
            }

            var elapsed = Time.time - actionStartedAt;
            if (IsDead)
            {
                actionLength = 0f;
            }
            else if (Speed >= HumanLocomotion.MovingSpeed && elapsed < actionLength - HumanLocomotion.ActionFadeOutSeconds)
            {
                actionLength = elapsed + HumanLocomotion.ActionFadeOutSeconds;
            }

            ActionWeight = HumanLocomotion.ActionWeight(elapsed, actionLength);
            animator.SetLayerWeight(actionLayer, ActionWeight);
            if (ActionWeight <= 0f && elapsed > 0f)
            {
                actionStartedAt = -1f;
                animator.Play(Animator.StringToHash(HumanLocomotion.EmptyState), actionLayer, 0f);
            }
        }

        // A body that holds a tool plays its clip while it stands and lives, and crossfades back to
        // Idle, or to walking when it moves, when it lets go of the tool or starts to move.
        private void ApplyUse()
        {
            if (useStateId != 0 && usePathId == 0)
            {
                usePathId = Animator.StringToHash(animator.GetLayerName(0) + "." + UseClip);
            }

            var wanted = useStateId != 0 && !IsDead && Speed < HumanLocomotion.MovingSpeed
                && animator.HasState(0, usePathId)
                ? useStateId
                : 0;
            if (wanted != 0)
            {
                if (!InOrEntering(wanted))
                {
                    animator.CrossFadeInFixedTime(wanted, HumanLocomotion.UseCrossfadeSeconds, 0);
                }

                shownUseStateId = wanted;
                return;
            }

            if (shownUseStateId == 0)
            {
                return;
            }

            if (!IsDead && InOrEntering(shownUseStateId))
            {
                animator.CrossFadeInFixedTime(
                    Speed >= HumanLocomotion.MovingSpeed ? LocomotionStateId : IdleStateId,
                    HumanLocomotion.UseCrossfadeSeconds,
                    0);
            }
            else if (!InOrLeaving(shownUseStateId))
            {
                shownUseStateId = 0;
            }
        }

        private bool InOrEnteringDeath()
        {
            return animator.GetCurrentAnimatorStateInfo(0).shortNameHash == DeathStateId
                || (animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).shortNameHash == DeathStateId);
        }

        // In a state and not leaving it, or crossfading into it.
        private bool InOrEntering(int stateId)
        {
            return (animator.GetCurrentAnimatorStateInfo(0).shortNameHash == stateId && !animator.IsInTransition(0))
                || (animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).shortNameHash == stateId);
        }

        private bool InOrLeaving(int stateId)
        {
            return animator.GetCurrentAnimatorStateInfo(0).shortNameHash == stateId;
        }
    }
}
