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

        private int tickRateHz;
        private int interpolationDelayTicks;
        private float latestSnapshotReceivedAt;

        public string CharacterId { get; private set; }

        public float SecondsSinceLastSnapshot
        {
            get { return Time.realtimeSinceStartup - latestSnapshotReceivedAt; }
        }

        public void Initialize(string characterId, int simulationTickRateHz, int delayTicks)
        {
            CharacterId = characterId;
            tickRateHz = Mathf.Max(1, simulationTickRateHz);
            interpolationDelayTicks = Mathf.Max(1, delayTicks);
            latestSnapshotReceivedAt = Time.realtimeSinceStartup;
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
            if (!interpolation.Push(serverTick, movementState))
            {
                return;
            }

            latestSnapshotReceivedAt = Time.realtimeSinceStartup;
            ApplyInterpolatedState(interpolation.LatestServerTick);
        }

        private void Update()
        {
            if (interpolation.Count == 0)
            {
                return;
            }

            var elapsedTicks = (Time.realtimeSinceStartup - latestSnapshotReceivedAt) * tickRateHz;
            var targetTick = interpolation.LatestServerTick + elapsedTicks - interpolationDelayTicks;
            ApplyInterpolatedState(targetTick);
        }

        private void ApplyInterpolatedState(double targetTick)
        {
            if (!interpolation.TrySample(targetTick, out var state))
            {
                return;
            }

            transform.SetPositionAndRotation(
                new Vector3(state.PositionX, state.PositionY, state.PositionZ),
                Quaternion.Euler(0f, state.YawDegrees, 0f));
        }
    }
}
