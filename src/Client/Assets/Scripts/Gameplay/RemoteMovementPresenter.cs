using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Presents a remote entity from its buffered server states: an adaptive render clock two
    // snapshot intervals behind the server, interpolation between the states around it, and
    // the grounded presentation that removes the slope support offset and blends small steps.
    // Remote players and moving Mobs share it. Presentation only; nothing feeds back into
    // prediction or the worker.
    public sealed class RemoteMovementPresenter
    {
        public const float MinimumInterpolationDelaySeconds = 0.1f;
        private const int MinimumInterpolationDelayTicks = 2;

        // One snapshot interval covers the wait for the next snapshot, and the second absorbs
        // a snapshot that arrives up to one interval late.
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
        private ICollisionWorld collisionWorld;
        private CharacterCollisionSettings collisionSettings;
        private float presentedTargetHeight;

        public int TickRateHz
        {
            get { return tickRateHz; }
        }

        public int DelayTicks
        {
            get { return interpolationDelayTicks; }
        }

        // Whether a pose was presented since the last clear.
        public bool HasPose
        {
            get { return interpolation.Count > 0 && renderClock.IsInitialized; }
        }

        // Whether the render clock reached the newest state, so nothing is left to interpolate.
        public bool IsPlayedOut
        {
            get { return HasPose && renderClock.CurrentTick >= interpolation.LatestServerTick; }
        }

        // Whether the presented pose no longer changes: the render clock reached the newest
        // state and the grounded height finished blending.
        public bool IsSettled
        {
            get
            {
                return IsPlayedOut
                    && groundedVerticalPresentation.CurrentHeight == presentedTargetHeight;
            }
        }

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
        }

        // Buffers a server state and returns false for one that is not newer than the newest.
        // The first state after a clear starts the render clock and is presented at once.
        public bool Push(uint serverTick, PlayerMovementState state)
        {
            var wasEmpty = interpolation.Count == 0;
            if (!interpolation.Push(serverTick, state))
            {
                return false;
            }

            if (wasEmpty)
            {
                Present(renderClock.Reset(interpolation.LatestServerTick, interpolationDelayTicks), 0f);
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

            Present(renderClock.Reset(interpolation.LatestServerTick, interpolationDelayTicks), 0f);
        }

        public bool TryGetNewestState(out uint serverTick, out PlayerMovementState state)
        {
            serverTick = interpolation.LatestRawServerTick;
            return interpolation.TryGetNewest(out state);
        }

        // Advances the render clock by one frame and presents the state at it. Returns
        // whether there is a pose to present.
        public bool Advance(float unscaledDeltaTime, float deltaTime)
        {
            if (!HasPose)
            {
                return false;
            }

            Present(
                renderClock.Advance(
                    unscaledDeltaTime,
                    tickRateHz,
                    interpolation.LatestServerTick,
                    interpolationDelayTicks),
                deltaTime);
            return true;
        }

        private void Present(double targetTick, float deltaTime)
        {
            if (!interpolation.TrySample(targetTick, out var state))
            {
                return;
            }

            var position = new Vector3(state.PositionX, state.PositionY, state.PositionZ);
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
