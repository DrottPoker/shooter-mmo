using System;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Presents a remote entity from its buffered server states: an adaptive render clock behind
    // the server, interpolation between the states around it, and the grounded presentation
    // that removes the slope support offset and blends small steps. Remote players and moving
    // Mobs share it. Presentation only; nothing feeds back into prediction or the worker.
    //
    // The states of one entity arrive at uneven intervals: every snapshot near the player,
    // every few snapshots farther away, and once a second while it stands. The render delay
    // follows the interval at which the entity's moving states arrive and changes slowly. A
    // state at the same place as the one before means the entity stands there now. When the
    // newest state of a walking entity is older than the render time, the pose continues along
    // its velocity for a while and then eases to a stop; a newer state then takes over without
    // a jump, its difference blended out at a bounded speed.
    public sealed class RemoteMovementPresenter
    {
        public const float MinimumInterpolationDelaySeconds = 0.1f;

        // The longest render delay an entity's intervals may ask for.
        public const float MaximumInterpolationDelaySeconds = 0.5f;

        // How long the pose continues along the velocity, and then how long it eases to a stop.
        public const float ExtrapolationSeconds = 0.25f;
        public const float ExtrapolationEaseSeconds = 0.25f;

        // The speed at which a difference between the shown pose and the states is blended out,
        // and the difference beyond which the pose snaps instead.
        public const float CorrectionMetresPerSecond = 3f;
        public const float SnapDistance = 3f;

        // How fast the render delay follows a changed interval: ticks of delay per tick of time.
        public const float DelayChangePerTick = 0.125f;

        private const int MinimumInterpolationDelayTicks = 2;

        // The share of the way each newly observed interval moves the estimate.
        private const double IntervalSmoothing = 0.25d;

        // A velocity below 0.1 m/s counts as standing.
        private const float StandingSpeedSquared = 0.01f;

        // One snapshot interval covers the wait for the next snapshot, and the second absorbs
        // a snapshot that arrives up to one interval late. The same holds for the interval at
        // which an entity's own states arrive.
        private const int InterpolationDelaySnapshotIntervals = 2;

        private readonly RemoteMovementInterpolation interpolation =
            new RemoteMovementInterpolation();
        private readonly RemoteRenderClock renderClock = new RemoteRenderClock();
        private readonly GroundedVerticalPresentation groundedVerticalPresentation =
            new GroundedVerticalPresentation();
        private readonly CollisionQueryBuffer presentationGroundQuery =
            new CollisionQueryBuffer();

        private int tickRateHz = 1;
        private int interpolationDelayTicks = 1;
        private double currentDelayTicks = 1d;
        private double observedIntervalTicks = 1d;
        private ICollisionWorld collisionWorld;
        private CharacterCollisionSettings collisionSettings;
        private float presentedTargetHeight;
        private Vector2 correction;

        // While the pose rests: whether the render clock follows the server estimate exactly, and
        // the unscaled seconds of the frames since then, which the clock catches up on at once.
        private bool resting;
        private bool restingFollowsEstimate;
        private double restingSeconds;

        public int TickRateHz
        {
            get { return tickRateHz; }
        }

        // The render delay the presenter starts with and never goes below.
        public int DelayTicks
        {
            get { return interpolationDelayTicks; }
        }

        // The render delay now, which follows the interval of the entity's moving states.
        public double CurrentDelayTicks
        {
            get { return currentDelayTicks; }
        }

        // The longest gap after a first state that a moving second state walked all of, the
        // longest render delay; a longer gap counts as a stand.
        // A second state closer than this to a first state of unknown standing takes its place:
        // one snapshot interval, the shortest gap between two records of an entity.
        public long FirstStateRefreshTicks
        {
            get { return Math.Max(1L, (long)Math.Round(interpolationDelayTicks / (double)InterpolationDelaySnapshotIntervals)); }
        }

        public long LongestWalkGapTicks
        {
            get { return (long)Math.Round(MaximumInterpolationDelaySeconds * tickRateHz); }
        }

        // The render delay the observed interval asks for.
        public double TargetDelayTicks
        {
            get
            {
                var maximum = Math.Max(interpolationDelayTicks, MaximumInterpolationDelaySeconds * tickRateHz);
                return Math.Min(maximum, Math.Max(interpolationDelayTicks, InterpolationDelaySnapshotIntervals * observedIntervalTicks));
            }
        }

        // Whether a pose was presented since the last clear.
        public bool HasPose
        {
            get { return interpolation.Count > 0 && renderClock.IsInitialized; }
        }

        // Whether the render time reached the newest state, so nothing is left to interpolate.
        public bool IsPlayedOut
        {
            get { return HasPose && renderClock.RenderTick >= interpolation.LatestServerTick; }
        }

        // Whether the pose continues along the velocity past the newest state of a walking entity.
        public bool IsExtrapolating
        {
            get { return IsPlayedOut && OverrunSeconds > 0f && IsWalking(out _, out _); }
        }

        // How long the render time is past the newest state.
        public float OverrunSeconds
        {
            get { return OverrunSecondsAt(renderClock.RenderTick); }
        }

        // Whether the presented pose no longer changes: the render clock reached the newest
        // state, the grounded height finished blending, and nothing is extrapolated or blended.
        public bool IsSettled
        {
            get
            {
                return IsPlayedOut
                    && !IsExtrapolating
                    && correction == Vector2.zero
                    && groundedVerticalPresentation.CurrentHeight == presentedTargetHeight;
            }
        }

        // Whether the entity ran out of states to show while it walked: the render time passed
        // the newest state, the entity still moved between its two newest states, and the
        // extrapolation ran out, so the pose stands where the next state should have taken it
        // on. An entity that stopped is not starved, also when its states come a second apart.
        public bool IsStarved
        {
            get
            {
                return IsPlayedOut
                    && OverrunSeconds > ExtrapolationSeconds + ExtrapolationEaseSeconds
                    && IsWalking(out _, out _);
            }
        }

        // Whether the pose rests on a standing newest state: it reached that state, nothing is
        // extrapolated or blended, and the delay is the one the entity asks for, so the pose
        // does not change until the next state or new ground under it. Advance then presents
        // nothing, as it describes.
        public bool IsResting
        {
            get { return resting; }
        }

        // The revision of the ground the pose was last presented on, as Advance was given it.
        public int GroundRevision { get; private set; }

        // How many times the presented pose jumped rather than moved: the first state after a clear,
        // every restart of the render clock, and every snap of a correction. The body's loose cloth
        // is carried along across them.
        public int Discontinuities { get; private set; }

        // The presented ground point and facing.
        public Vector3 Position { get; private set; }

        public float YawDegrees { get; private set; }

        public Quaternion Rotation
        {
            get { return Quaternion.Euler(0f, YawDegrees, 0f); }
        }

        // The render delay in ticks behind the server: two snapshot intervals, and at least
        // 100 ms. At 30 ticks and 15 snapshots per second that is four ticks, about 133 ms.
        public static int InterpolationDelayTicks(int tickRateHz, int snapshotRateHz)
        {
            var ticks = Mathf.Max(1, tickRateHz);
            var snapshotIntervalTicks = Mathf.Max(
                1,
                Mathf.RoundToInt(ticks / (float)Mathf.Clamp(snapshotRateHz, 1, ticks)));
            return Mathf.Max(
                Mathf.Max(MinimumInterpolationDelayTicks, Mathf.CeilToInt(ticks * MinimumInterpolationDelaySeconds)),
                snapshotIntervalTicks * InterpolationDelaySnapshotIntervals);
        }

        // The state of a remote record. The record carries no velocity: the presenter derives it
        // from the positions of its two newest states.
        public static PlayerMovementState ToMovementState(RealtimeEntitySnapshot record)
        {
            return new PlayerMovementState(
                record.PositionX,
                record.PositionY,
                record.PositionZ,
                0f,
                0f,
                0f,
                record.YawDegrees,
                record.IsGrounded);
        }

        public static PlayerMovementState ToMovementState(RealtimePlayerState state)
        {
            return new PlayerMovementState(
                state.PositionX,
                state.PositionY,
                state.PositionZ,
                state.VelocityX,
                state.VelocityY,
                state.VelocityZ,
                state.YawDegrees,
                state.IsGrounded);
        }

        // The seconds that the extrapolation adds along the velocity after the newest state: the
        // overrun for ExtrapolationSeconds, then easing to a stop over ExtrapolationEaseSeconds.
        public static float ExtrapolatedSeconds(float overrunSeconds)
        {
            if (overrunSeconds <= 0f)
            {
                return 0f;
            }

            if (overrunSeconds <= ExtrapolationSeconds)
            {
                return overrunSeconds;
            }

            var easing = Math.Min(overrunSeconds - ExtrapolationSeconds, ExtrapolationEaseSeconds);
            return ExtrapolationSeconds + easing - (easing * easing / (2f * ExtrapolationEaseSeconds));
        }

        // Sets the timing and collision of the presentation and forgets every state.
        public void Configure(
            int simulationTickRateHz,
            int delayTicks,
            ICollisionWorld worldCollision,
            CharacterCollisionSettings characterCollision)
        {
            tickRateHz = Mathf.Max(1, simulationTickRateHz);
            interpolationDelayTicks = Mathf.Max(1, delayTicks);
            collisionWorld = worldCollision;
            collisionSettings = characterCollision;
            Clear();
        }

        public void Clear()
        {
            interpolation.Clear();
            renderClock.Clear();
            groundedVerticalPresentation.Clear();
            currentDelayTicks = interpolationDelayTicks;
            observedIntervalTicks = interpolationDelayTicks / (double)InterpolationDelaySnapshotIntervals;
            correction = Vector2.zero;
            StopResting();
        }

        // Buffers a server state and returns false for one that is not newer than the newest.
        // The first state after a clear starts the render clock and is presented at once; it
        // says nothing about standing, so a moving second state at most
        // MaximumInterpolationDelaySeconds after it walked the whole gap. A newer state keeps
        // the shown pose where it was, also when the pose was extrapolated; the difference is
        // blended out.
        public bool Push(uint serverTick, PlayerMovementState state)
        {
            return Push(serverTick, state, false);
        }

        // Buffers a state that the entity is known to stand at, such as a reliable rest pose. As
        // the first state after a clear, a moving state after it starts from a stand, as between
        // later states.
        public bool PushStanding(uint serverTick, PlayerMovementState state)
        {
            return Push(serverTick, state, true);
        }

        private bool Push(uint serverTick, PlayerMovementState state, bool firstStateStands)
        {
            Wake();
            var wasEmpty = interpolation.Count == 0;
            var renderTick = renderClock.RenderTick;
            var shown = Vector2.zero;
            var keepShown = !wasEmpty && HasPose && TrySampleShown(renderTick, out shown);
            if (!interpolation.Push(
                    serverTick,
                    state,
                    (long)Math.Round(observedIntervalTicks),
                    LongestWalkGapTicks,
                    firstStateStands,
                    FirstStateRefreshTicks))
            {
                return false;
            }

            if (wasEmpty)
            {
                Present(renderClock.Reset(interpolation.LatestServerTick, currentDelayTicks), 0f);
                Discontinuities++;
                return true;
            }

            // A moving state after a moving one tells the interval; one late state moves the
            // estimate only a quarter of the way. The first interval an entity walked is all
            // there is to go by, so it is the estimate at once.
            if (interpolation.LastMovingIntervalTicks > 0)
            {
                observedIntervalTicks = interpolation.IsFirstMovingInterval
                    ? interpolation.LastMovingIntervalTicks
                    : observedIntervalTicks + (IntervalSmoothing * (interpolation.LastMovingIntervalTicks - observedIntervalTicks));
            }

            // The first state was shown standing while the render time ran on past it, because
            // nothing said that the entity walked. The render time goes back to it, and the delay
            // grows to at least the gap, so the walk starts from the shown pose at the entity's
            // own speed instead of blending the lag out on top of it.
            var firstTick = interpolation.LatestServerTick - interpolation.LastMovingIntervalTicks;
            if (interpolation.IsFirstMovingInterval && renderTick > firstTick)
            {
                currentDelayTicks = Math.Max(currentDelayTicks, interpolation.LastMovingIntervalTicks);
                renderTick = renderClock.Reset(interpolation.LatestServerTick, currentDelayTicks);
            }

            if (keepShown && TrySampleShown(renderTick, out var next))
            {
                correction += shown - next;
                if (correction.magnitude > SnapDistance)
                {
                    correction = Vector2.zero;
                    Discontinuities++;
                }
            }

            return true;
        }

        // Starts the render clock again one delay behind the newest state and presents it.
        public void RestartClock()
        {
            if (interpolation.Count == 0)
            {
                return;
            }

            StopResting();
            correction = Vector2.zero;
            Present(renderClock.Reset(interpolation.LatestServerTick, currentDelayTicks), 0f);
            Discontinuities++;
        }

        public bool TryGetNewestState(out uint serverTick, out PlayerMovementState state)
        {
            serverTick = interpolation.LatestRawServerTick;
            return interpolation.TryGetNewest(out state);
        }

        // The planar velocity in meters per second from the two newest states, which stands in
        // for the velocity that a remote record does not carry. False with fewer than two.
        public bool TryGetDerivedVelocity(out float velocityX, out float velocityZ)
        {
            return interpolation.TryGetNewestPlanarVelocity(tickRateHz, out velocityX, out velocityZ);
        }

        // Advances the render clock by one frame and presents the state at it, on the ground of
        // the given revision, which changes when the ground may have, such as when a collision
        // chunk loaded. Returns whether it presented a pose.
        //
        // A resting pose presents nothing and asks the collision world for nothing. Until the
        // render time follows the server estimate exactly, a frame only advances the clock; after
        // that it only adds up its time, which the clock catches up on in one step at the next
        // state, or when the ground revision changed and the pose lands on the new ground. The
        // clock then stands where advancing it frame by frame would have it, so the delay, the
        // walk after a stand, and every other rule show exactly the same.
        public bool Advance(float unscaledDeltaTime, float deltaTime, int groundRevision)
        {
            if (!HasPose)
            {
                return false;
            }

            if (resting)
            {
                if (groundRevision == GroundRevision)
                {
                    Rest(unscaledDeltaTime);
                    return false;
                }

                Wake();
            }

            FollowTargetDelay(unscaledDeltaTime);
            renderClock.Advance(
                unscaledDeltaTime,
                tickRateHz,
                interpolation.LatestServerTick,
                currentDelayTicks);
            var step = CorrectionMetresPerSecond * Math.Max(0f, unscaledDeltaTime);
            correction = correction.magnitude <= step ? Vector2.zero : correction - (correction.normalized * step);
            Present(renderClock.RenderTick, deltaTime);
            GroundRevision = groundRevision;
            resting = !interpolation.NewestMoved && currentDelayTicks == TargetDelayTicks && IsSettled;
            restingFollowsEstimate = resting && FollowsEstimate;
            return true;
        }

        // Advances on ground that does not change.
        public bool Advance(float unscaledDeltaTime, float deltaTime)
        {
            return Advance(unscaledDeltaTime, deltaTime, GroundRevision);
        }

        private bool FollowsEstimate
        {
            get { return renderClock.FollowsEstimate(tickRateHz, interpolation.LatestServerTick, currentDelayTicks); }
        }

        // A frame of a resting pose. Nothing it shows changes, so only the clock moves on, and
        // once it follows the estimate exactly, only the time is counted.
        private void Rest(float unscaledDeltaTime)
        {
            if (restingFollowsEstimate)
            {
                restingSeconds += unscaledDeltaTime;
                return;
            }

            renderClock.Advance(
                unscaledDeltaTime,
                tickRateHz,
                interpolation.LatestServerTick,
                currentDelayTicks);
            restingFollowsEstimate = FollowsEstimate;
        }

        // Ends a rest: the clock catches up on the time it only counted.
        private void Wake()
        {
            if (restingSeconds > 0d)
            {
                renderClock.Advance(
                    (float)restingSeconds,
                    tickRateHz,
                    interpolation.LatestServerTick,
                    currentDelayTicks);
            }

            StopResting();
        }

        private void StopResting()
        {
            resting = false;
            restingFollowsEstimate = false;
            restingSeconds = 0d;
        }

        private float OverrunSecondsAt(double renderTick)
        {
            return (float)(Math.Max(0d, renderTick - interpolation.LatestServerTick) / tickRateHz);
        }

        // The delay moves toward what the observed interval asks for at a bounded rate, so the
        // render clock changes its speed only a little when an entity changes its tier.
        private void FollowTargetDelay(float unscaledDeltaTime)
        {
            var target = TargetDelayTicks;
            var step = DelayChangePerTick * tickRateHz * Math.Max(0f, unscaledDeltaTime);
            currentDelayTicks = Math.Abs(target - currentDelayTicks) <= step
                ? target
                : currentDelayTicks + (Math.Sign(target - currentDelayTicks) * step);
        }

        // The walking velocity of the two newest states; false when the newest state stands.
        private bool IsWalking(out float velocityX, out float velocityZ)
        {
            if (!interpolation.NewestMoved || !TryGetDerivedVelocity(out velocityX, out velocityZ))
            {
                velocityX = 0f;
                velocityZ = 0f;
                return false;
            }

            return (velocityX * velocityX) + (velocityZ * velocityZ) > StandingSpeedSquared;
        }

        // The planar point at the tick, extrapolated past the newest state of a walking entity.
        private bool TrySampleShown(double targetTick, out Vector2 point)
        {
            if (!interpolation.TrySample(targetTick, out var state))
            {
                point = Vector2.zero;
                return false;
            }

            point = new Vector2(state.PositionX, state.PositionZ);
            if (targetTick > interpolation.LatestServerTick && IsWalking(out var velocityX, out var velocityZ))
            {
                var seconds = ExtrapolatedSeconds(OverrunSecondsAt(targetTick));
                point += new Vector2(velocityX * seconds, velocityZ * seconds);
            }

            return true;
        }

        private void Present(double targetTick, float deltaTime)
        {
            if (!interpolation.TrySample(targetTick, out var state))
            {
                return;
            }

            if (TrySampleShown(targetTick, out var planar))
            {
                planar += correction;
            }

            var position = new Vector3(planar.x, state.PositionY, planar.y);
            var shouldSmoothGroundedHeight = false;
            if (GroundedMovementPresentation.TryGetVisualHeight(
                    position.x,
                    position.y,
                    position.z,
                    state.IsGrounded,
                    collisionWorld,
                    collisionSettings,
                    presentationGroundQuery,
                    out var visualHeight,
                    out var groundNormal))
            {
                position.y = visualHeight;
                shouldSmoothGroundedHeight = GroundedMovementPresentation.IsFlatGround(
                    groundNormal);
            }

            presentedTargetHeight = position.y;
            var maximumSmoothDistance = collisionSettings != null
                ? collisionSettings.StepHeight + collisionSettings.GroundSnapDistance
                : 0f;
            position.y = groundedVerticalPresentation.Update(
                position.y,
                state.IsGrounded && shouldSmoothGroundedHeight,
                maximumSmoothDistance,
                GroundedMovementPresentation.StepSmoothingDurationSeconds,
                deltaTime);

            Position = position;
            YawDegrees = state.YawDegrees;
        }
    }
}
