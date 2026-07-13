using System;
using System.Collections.Generic;
using ShooterMmo.Collision;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;

namespace ShooterMmo.Networking
{
    public sealed class NetworkMovementSession
    {
        private NetworkMovementSession(
            string characterId,
            ulong controlledEntityId,
            MovementSimulationSettings settings,
            int snapshotRateHz,
            PlayerMovementState initialState,
            UnityWorldCollisionStream collisionStream)
        {
            CharacterId = characterId;
            ControlledEntityId = controlledEntityId;
            Settings = settings;
            SnapshotRateHz = snapshotRateHz;
            InitialState = initialState;
            CollisionStream = collisionStream;
            CollisionWorld = collisionStream.CollisionWorld;
        }

        public string CharacterId { get; private set; }

        public ulong ControlledEntityId { get; private set; }

        public MovementSimulationSettings Settings { get; private set; }

        public int SnapshotRateHz { get; private set; }

        public PlayerMovementState InitialState { get; private set; }

        public ChunkedStaticCollisionWorld CollisionWorld { get; private set; }

        public UnityWorldCollisionStream CollisionStream { get; private set; }

        public bool TryRefreshCollisionStreaming(
            IEnumerable<SimulationVector3> anchors,
            out string error)
        {
            return CollisionStream.TryRefresh(anchors, out error);
        }

        public bool TryEnsureCollisionChunks(
            IEnumerable<SimulationVector3> anchors,
            out string error)
        {
            return CollisionStream.TryEnsureLoaded(anchors, out error);
        }

        public static bool TryCreate(
            RealtimeJoinAccepted accepted,
            out NetworkMovementSession session,
            out string error)
        {
            session = null;
            error = string.Empty;
            if (accepted == null
                || accepted.MovementSettings == null
                || accepted.InitialPlayerState == null)
            {
                error = "WorldServer did not provide movement simulation configuration.";
                return false;
            }

            if (accepted.ControlledEntityId == 0)
            {
                error = "WorldServer did not assign a controlled network entity.";
                return false;
            }

            if (!string.Equals(
                    accepted.SimulationRevision,
                    GameSimulationCompatibility.Revision,
                    StringComparison.Ordinal))
            {
                error = "Client movement simulation revision does not match WorldServer. Client: "
                    + GameSimulationCompatibility.Revision + ", server: "
                    + accepted.SimulationRevision + ".";
                return false;
            }

            if (!UnityWorldCollisionLoader.TryCreateStream(
                    accepted.WorldId,
                    out var collisionStream,
                    out error))
            {
                return false;
            }

            if (!string.Equals(
                    accepted.CollisionRevision,
                    collisionStream.CollisionWorld.Revision,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "Client collision revision does not match WorldServer. Client: "
                    + collisionStream.CollisionWorld.Revision + ", server: " + accepted.CollisionRevision + ".";
                return false;
            }

            var initialState = accepted.InitialPlayerState;
            if (!collisionStream.TryRefresh(
                    new[]
                    {
                        new SimulationVector3(
                            initialState.PositionX,
                            initialState.PositionY,
                            initialState.PositionZ)
                    },
                    out error)
                || collisionStream.CollisionWorld.ChunkCount == 0)
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "No collision chunk is available around the initial player position.";
                }

                return false;
            }

            try
            {
                var source = accepted.MovementSettings;
                var collisionSettings = new CharacterCollisionSettings(
                    source.CharacterRadius,
                    source.CharacterHeight,
                    source.StepHeight,
                    source.MaximumSlopeDegrees,
                    source.GroundSnapDistance,
                    source.MaximumSubstepDistance,
                    source.MaximumPenetrationIterations);
                var settings = new MovementSimulationSettings(
                    source.TickRateHz,
                    source.WalkSpeed,
                    source.SprintSpeed,
                    source.RotationSpeedDegrees,
                    source.Gravity,
                    source.MaximumFallSpeed,
                    source.JumpVelocity,
                    source.GroundedVerticalVelocity,
                    source.GroundHeight,
                    source.MinimumX,
                    source.MaximumX,
                    source.MinimumZ,
                    source.MaximumZ,
                    collisionSettings);
                if (source.SnapshotRateHz <= 0
                    || source.SnapshotRateHz > source.TickRateHz
                    || source.TickRateHz % source.SnapshotRateHz != 0)
                {
                    error = "WorldServer movement snapshot rate is invalid.";
                    return false;
                }

                var state = accepted.InitialPlayerState;
                session = new NetworkMovementSession(
                    accepted.CharacterId,
                    accepted.ControlledEntityId,
                    settings,
                    source.SnapshotRateHz,
                    new PlayerMovementState(
                        state.PositionX,
                        state.PositionY,
                        state.PositionZ,
                        state.VelocityX,
                        state.VelocityY,
                        state.VelocityZ,
                        state.YawDegrees,
                        state.IsGrounded,
                        state.IsSprinting),
                    collisionStream);
                return true;
            }
            catch (ArgumentException exception)
            {
                error = "WorldServer movement configuration is invalid: " + exception.Message;
                return false;
            }
        }
    }
}
