using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using ShooterMmo.Networking;
using UnityEngine;

namespace ShooterMmo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class RemotePlayerView : MonoBehaviour
    {
        public const string SupportedArchetypeId = "player.default";

        private readonly RemoteMovementInterpolation interpolation =
            new RemoteMovementInterpolation();
        private readonly CollisionQueryBuffer presentationGroundQuery =
            new CollisionQueryBuffer();
        private readonly GroundedVerticalPresentation groundedVerticalPresentation =
            new GroundedVerticalPresentation();
        private readonly RemoteRenderClock renderClock = new RemoteRenderClock();

        private int tickRateHz;
        private int interpolationDelayTicks;
        private ICollisionWorld collisionWorld;
        private CharacterCollisionSettings collisionSettings;

        public ulong NetworkEntityId { get; private set; }

        public string PersistentId { get; private set; }

        public string DisplayName { get; private set; }

        public string ArchetypeId { get; private set; }

        public void Initialize(
            ulong networkEntityId,
            string persistentId,
            string displayName,
            string archetypeId,
            int simulationTickRateHz,
            int delayTicks,
            ICollisionWorld worldCollision,
            CharacterCollisionSettings characterCollision)
        {
            NetworkEntityId = networkEntityId;
            PersistentId = persistentId;
            DisplayName = displayName;
            ArchetypeId = archetypeId;
            tickRateHz = Mathf.Max(1, simulationTickRateHz);
            interpolationDelayTicks = Mathf.Max(1, delayTicks);
            collisionWorld = worldCollision;
            collisionSettings = characterCollision;
            groundedVerticalPresentation.Clear();
            renderClock.Clear();
            name = "RemotePlayer_" + networkEntityId + "_" + displayName;
        }

        public void PushSnapshot(uint serverTick, RealtimePlayerState state)
        {
            if (state == null || NetworkEntityId == 0)
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
