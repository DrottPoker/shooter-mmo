using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;

namespace Mmorpg.SimulationWorker.Realtime;

// Maps authoritative simulation state to realtime protocol and interest records. It is
// stateless, so any thread may call it.
internal static class RealtimeStateMapper
{
    public static RealtimeMovementSettings ToRealtimeMovementSettings(
        SimulationWorkerConfig config)
    {
        var settings = config.MovementSimulation;
        var collision = settings.CharacterCollision;
        return new RealtimeMovementSettings(
            (ushort)settings.TickRateHz,
            (ushort)config.SnapshotRateHz,
            settings.WalkSpeed,
            settings.RotationSpeedDegrees,
            settings.Gravity,
            settings.MaximumFallSpeed,
            settings.GroundedVerticalVelocity,
            settings.GroundHeight,
            settings.MinimumX,
            settings.MaximumX,
            settings.MinimumZ,
            settings.MaximumZ,
            collision.Radius,
            collision.Height,
            collision.StepHeight,
            collision.MaximumSlopeDegrees,
            collision.GroundSnapDistance,
            collision.MaximumSubstepDistance,
            (byte)collision.MaximumPenetrationIterations);
    }

    public static RealtimePlayerState ToRealtimePlayerState(PlayerMovementState state)
    {
        return new RealtimePlayerState(
            state.PositionX,
            state.PositionY,
            state.PositionZ,
            state.VelocityX,
            state.VelocityY,
            state.VelocityZ,
            state.YawDegrees,
            state.IsGrounded);
    }

    public static RealtimeMovementPathProgress ToRealtimePathProgress(MovementPathProgress progress)
    {
        return new RealtimeMovementPathProgress(
            progress.PathId,
            (byte)progress.NextWaypointIndex,
            (byte)progress.BlockedTicks);
    }

    public static RealtimeCarryState ToRealtimeCarryState(PlayerCarryState carryState)
    {
        return new RealtimeCarryState(
            carryState.ItemStateRevision,
            carryState.CarriedWeight,
            carryState.CarryCapacity);
    }

    public static SimulationInterestEntity ToInterestEntity(
        PlayerSimulationEntity entity)
    {
        return new SimulationInterestEntity(
            entity.NetworkEntityId,
            entity.Movement.State.PositionX,
            entity.Movement.State.PositionZ);
    }
}
