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
    // whenever it stands and lives. While a hand holds a weapon with a hold clip, such as a staff in
    // the main hand or a bow in the off hand, that hand's arm keeps that pose, and when the weapon's
    // attack starts the body plays the weapon's attack clip once, such as Cast, timed to strike when
    // the attack releases, and turns to face the attack's target while it stands.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
    [DefaultExecutionOrder(PresentationExecutionOrder.Locomotion)]
    public sealed class CharacterLocomotionAnimator : MonoBehaviour
    {
        // Short, so a character that stops does not keep walking on the spot.
        public const float SmoothingSeconds = 0.08f;
        public const float TeleportSpeed = 20f;

        // A standing body that attacks turns to face its target at this speed, and keeps facing it
        // this long after its attack clip ends, so attacks in a row do not turn it back and forth.
        public const float FacingDegreesPerSecond = 720f;
        public const float FacingHoldSeconds = 1.5f;

        private static readonly int SpeedId = Animator.StringToHash(HumanLocomotion.SpeedParameter);
        private static readonly int MovingId = Animator.StringToHash(HumanLocomotion.MovingParameter);
        private static readonly int RateId = Animator.StringToHash(HumanLocomotion.RateParameter);
        private static readonly int DeadId = Animator.StringToHash(HumanLocomotion.DeadParameter);
        private static readonly int DeathStateId = Animator.StringToHash(HumanLocomotion.DeathState);
        private static readonly int IdleStateId = Animator.StringToHash(HumanLocomotion.IdleState);
        private static readonly int LocomotionStateId = Animator.StringToHash(HumanLocomotion.LocomotionState);
        private static readonly int EmptyStateId = Animator.StringToHash(HumanLocomotion.EmptyState);

        private readonly HoldLayer mainHold = new HoldLayer(HumanLocomotion.MainHandHoldLayer);
        private readonly HoldLayer offHold = new HoldLayer(HumanLocomotion.OffHandHoldLayer);

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
        private int actionLayer = -1;
        private int actionStateId;
        // When the attack clip's start is by Time.time, ahead for one that waits to start, the time
        // in the clip it shows from, whether it shows, whether it ends early, and whether the body
        // stood still since it began.
        private float actionStartedAt = -1f;
        private float actionShownFrom;
        private bool actionShown;
        private bool actionEnding;
        private bool actionStoodStill;
        private float actionLength;
        // The target the body faces while it attacks, until when it keeps facing it, its own turn
        // below its character, and how far it turned from there.
        private Transform facingTarget;
        private float facingUntil = -1f;
        private Quaternion ownRotation = Quaternion.identity;
        private float facingDegrees;

        // The clip of the tool the body holds, such as Mine, or empty.
        public string UseClip { get; private set; } = string.Empty;

        // The pose the main hand's arm holds its weapon in, such as HoldStaff, or empty.
        public string HoldClip
        {
            get { return mainHold.Clip; }
        }

        // The pose the off hand's arm holds its weapon in, such as HoldBow, or empty.
        public string OffHandHoldClip
        {
            get { return offHold.Clip; }
        }

        // The attack clip the body plays or waits to play, such as Cast, or empty.
        public string ActionClip { get; private set; } = string.Empty;

        // How much of the body's pose comes from an attack clip that plays, from 0 to 1.
        public float ActionWeight { get; private set; }

        // Holds the arm of a hand's socket in a weapon's pose, such as HoldStaff for the main hand or
        // HoldBow for the off hand, or lets it move with the other clips again for an empty clip.
        public void Hold(string clip, string socketBone = HumanLocomotion.MainHandSocketBone)
        {
            var hold = string.Equals(socketBone, HumanLocomotion.OffHandSocketBone, System.StringComparison.Ordinal)
                ? offHold
                : mainHold;
            clip = clip ?? string.Empty;
            if (string.Equals(clip, hold.Clip, System.StringComparison.Ordinal))
            {
                return;
            }

            hold.Clip = clip;
            hold.StateId = clip.Length > 0 ? Animator.StringToHash(clip) : 0;
            hold.Exists = null;
        }

        // Plays a weapon's attack clip once over the body, such as Cast, as if it had started clipTime
        // seconds ago, so a negative time starts it that much later; false when the body lies dead or
        // its controller has no state of that name on the action layer.
        public bool PlayAttack(string clip, float clipTime = 0f)
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

            ActionClip = clip;
            actionStateId = Animator.StringToHash(clip);
            actionLength = length;
            actionStartedAt = Time.time - clipTime;
            actionShownFrom = Mathf.Clamp(clipTime, 0f, length);
            actionShown = false;
            actionEnding = false;
            actionStoodStill = Speed < HumanLocomotion.MovingSpeed;
            if (clipTime >= 0f)
            {
                ShowAction(clipTime);
            }

            return true;
        }

        // Ends the attack clip with a short fade, or drops one that waits to start, as when its
        // attack was cancelled before it released.
        public void StopAttack()
        {
            if (ActionClip.Length == 0)
            {
                return;
            }

            if (actionShown)
            {
                EndActionEarly(Time.time - actionStartedAt);
            }
            else
            {
                EndAction();
            }
        }

        // Turns the body to face the target of its attack, such as an archer's mark, while it stands
        // and attacks and for FacingHoldSeconds after; the character itself keeps its facing.
        public void Face(Transform target)
        {
            facingTarget = target;
            facingUntil = Time.time + FacingHoldSeconds;
        }

        // How far the body turned from its character's facing to face its target, in degrees.
        public float FacingDegrees
        {
            get { return facingDegrees; }
        }

        // Whether the body plays the attack clip, neither waiting to start nor ending early, and how
        // many seconds of it have passed.
        public bool IsPlayingAttack(string clip, out float time)
        {
            time = Time.time - actionStartedAt;
            return actionShown && !actionEnding && time < actionLength
                && !string.IsNullOrEmpty(clip) && string.Equals(ActionClip, clip, System.StringComparison.Ordinal);
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
            ownRotation = transform.localRotation;
            var controller = animator.runtimeAnimatorController;
            if (controller != null)
            {
                mainHold.Index = animator.GetLayerIndex(mainHold.Name);
                offHold.Index = animator.GetLayerIndex(offHold.Name);
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
            ApplyFacing(deltaTime);
            Apply();
        }

        // A standing, living body turns toward the target of its attack while the attack clip
        // plays and for a while after, and back to its character's facing once it moves or the
        // while has passed.
        private void ApplyFacing(float deltaTime)
        {
            if (facingTarget == null && facingDegrees == 0f)
            {
                return;
            }

            if (ActionClip.Length > 0)
            {
                facingUntil = Mathf.Max(facingUntil, Time.time + FacingHoldSeconds);
            }

            var wanted = 0f;
            if (facingTarget != null && !IsDead && Speed < HumanLocomotion.MovingSpeed && Time.time < facingUntil)
            {
                var parent = transform.parent;
                var own = (parent != null ? parent.rotation : Quaternion.identity) * ownRotation * Vector3.forward;
                var toTarget = facingTarget.position - transform.position;
                own.y = 0f;
                toTarget.y = 0f;
                if (own.sqrMagnitude > 1e-6f && toTarget.sqrMagnitude > 1e-6f)
                {
                    wanted = Vector3.SignedAngle(own, toTarget, Vector3.up);
                }
            }
            else if (Time.time >= facingUntil)
            {
                facingTarget = null;
            }

            facingDegrees = deltaTime > 0f
                ? Mathf.MoveTowardsAngle(facingDegrees, wanted, FacingDegreesPerSecond * deltaTime)
                : wanted;
            if (Mathf.Abs(facingDegrees) < 0.01f && facingTarget == null)
            {
                facingDegrees = 0f;
            }

            transform.localRotation = Quaternion.AngleAxis(facingDegrees, Vector3.up) * ownRotation;
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
            ApplyHold(mainHold);
            ApplyHold(offHold);
            ApplyAction();
        }

        // A hand's arm holds the weapon's pose while the body lives and uses no tool. Bodies that
        // hold no such weapon set the layer only when they let go of one.
        private void ApplyHold(HoldLayer hold)
        {
            if (hold.Index < 0)
            {
                return;
            }

            if (hold.StateId != 0 && hold.Exists == null && animator.isInitialized)
            {
                hold.Exists = animator.HasState(hold.Index, Animator.StringToHash(hold.Name + "." + hold.Clip));
            }

            var holds = hold.StateId != 0 && hold.Exists == true && !IsDead && UseClip.Length == 0;
            if (!holds)
            {
                if (hold.Shown)
                {
                    animator.SetLayerWeight(hold.Index, 0f);
                    hold.Shown = false;
                }

                return;
            }

            // A rebound Animator starts its layers over, so the state and weight are set every frame.
            if (animator.GetCurrentAnimatorStateInfo(hold.Index).shortNameHash != hold.StateId)
            {
                animator.Play(hold.StateId, hold.Index, 0f);
            }

            animator.SetLayerWeight(hold.Index, 1f);
            hold.Shown = true;
        }

        // An attack clip that waits to start shows nothing until its start; then it fades in from
        // where it starts to show, plays once, and fades out. A body that dies ends it at once, and
        // one that starts to move during it ends it with a short fade, or drops it before it shows; a
        // body that still moves as it begins, such as one that just came into reach, keeps it.
        private void ApplyAction()
        {
            if (actionLayer < 0 || ActionClip.Length == 0)
            {
                return;
            }

            if (IsDead)
            {
                EndAction();
                return;
            }

            var elapsed = Time.time - actionStartedAt;
            if (Speed < HumanLocomotion.MovingSpeed)
            {
                actionStoodStill = true;
            }
            else if (actionStoodStill)
            {
                if (!actionShown)
                {
                    EndAction();
                    return;
                }

                EndActionEarly(elapsed);
            }

            if (!actionShown)
            {
                if (elapsed < actionShownFrom)
                {
                    ActionWeight = 0f;
                    animator.SetLayerWeight(actionLayer, 0f);
                    return;
                }

                ShowAction(elapsed);
            }

            ActionWeight = HumanLocomotion.ActionWeight(elapsed, actionLength, actionShownFrom);
            animator.SetLayerWeight(actionLayer, ActionWeight);
            if (ActionWeight <= 0f && elapsed > actionShownFrom)
            {
                EndAction();
            }
        }

        private void ShowAction(float elapsed)
        {
            animator.Play(actionStateId, actionLayer, Mathf.Clamp01(elapsed / actionLength));
            actionShown = true;
        }

        // The clip fades out from now, unless it already ends sooner.
        private void EndActionEarly(float elapsed)
        {
            if (!actionEnding && elapsed < actionLength - HumanLocomotion.ActionFadeOutSeconds)
            {
                actionLength = elapsed + HumanLocomotion.ActionFadeOutSeconds;
            }

            actionEnding = true;
        }

        private void EndAction()
        {
            ActionClip = string.Empty;
            actionStartedAt = -1f;
            actionShown = false;
            actionEnding = false;
            ActionWeight = 0f;
            if (HasController && actionLayer >= 0)
            {
                animator.SetLayerWeight(actionLayer, 0f);
                animator.Play(EmptyStateId, actionLayer, 0f);
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

        // A layer of one arm that holds a weapon's pose: the clip, its state, whether the layer has a
        // state of the clip's name, once the Animator could say, and whether the pose shows.
        private sealed class HoldLayer
        {
            public HoldLayer(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public int Index = -1;
            public string Clip = string.Empty;
            public int StateId;
            public bool? Exists;
            public bool Shown;
        }
    }
}
