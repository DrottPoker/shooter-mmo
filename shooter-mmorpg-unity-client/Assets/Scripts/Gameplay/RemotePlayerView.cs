using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using ShooterMmo.Networking;
using UnityEngine;

namespace ShooterMmo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class RemotePlayerView : MonoBehaviour
    {
        private readonly RemoteMovementInterpolation interpolation =
            new RemoteMovementInterpolation();
        private readonly CollisionQueryBuffer presentationGroundQuery =
            new CollisionQueryBuffer();
        private readonly GroundedVerticalPresentation groundedVerticalPresentation =
            new GroundedVerticalPresentation();
        private readonly RemoteRenderClock renderClock = new RemoteRenderClock();

        private int tickRateHz;
        private int interpolationDelayTicks;
        private float latestSnapshotReceivedAt;
        private ICollisionWorld collisionWorld;
        private CharacterCollisionSettings collisionSettings;

        public string CharacterId { get; private set; }

        public float SecondsSinceLastSnapshot
        {
            get { return Time.realtimeSinceStartup - latestSnapshotReceivedAt; }
        }

        public void Initialize(
            string characterId,
            int simulationTickRateHz,
            int delayTicks,
            ICollisionWorld worldCollision,
            CharacterCollisionSettings characterCollision)
        {
            CharacterId = characterId;
            tickRateHz = Mathf.Max(1, simulationTickRateHz);
            interpolationDelayTicks = Mathf.Max(1, delayTicks);
            collisionWorld = worldCollision;
            collisionSettings = characterCollision;
            groundedVerticalPresentation.Clear();
            latestSnapshotReceivedAt = Time.realtimeSinceStartup;
            renderClock.Clear();
            name = "RemotePlayer_" + characterId;
        }

        public void PushSnapshot(uint serverTick, RealtimePlayerState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(CharacterId))
            {
                return;
            }

            var movementState = new PlayerMovementState(
                state.PositionX,
                state.PositionY,
                state.PositionZ,
                state.VelocityX,
                state.VelocityY,
                state.VelocityZ,
                state.YawDegrees,
                state.IsGrounded,
                state.IsSprinting);
            var wasEmpty = interpolation.Count == 0;
            if (!interpolation.Push(serverTick, movementState))
            {
                return;
            }

            latestSnapshotReceivedAt = Time.realtimeSinceStartup;
            if (wasEmpty)
            {
                ApplyInterpolatedState(renderClock.Reset(
                    interpolation.LatestServerTick,
                    interpolationDelayTicks));
            }
        }

        private void Update()
        {
            if (interpolation.Count == 0 || !renderClock.IsInitialized)
            {
                return;
            }

            ApplyInterpolatedState(renderClock.Advance(
                Time.unscaledDeltaTime,
                tickRateHz,
                interpolation.LatestServerTick,
                interpolationDelayTicks));
        }

        private void ApplyInterpolatedState(double targetTick)
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

            var maximumSmoothDistance = collisionSettings != null
                ? collisionSettings.StepHeight + collisionSettings.GroundSnapDistance
                : 0f;
            position.y = groundedVerticalPresentation.Update(
                position.y,
                state.IsGrounded && shouldSmoothGroundedHeight,
                maximumSmoothDistance,
                GroundedMovementPresentation.StepSmoothingDurationSeconds,
                Time.deltaTime);

            transform.SetPositionAndRotation(
                position,
                Quaternion.Euler(0f, state.YawDegrees, 0f));
        }
    }
}
