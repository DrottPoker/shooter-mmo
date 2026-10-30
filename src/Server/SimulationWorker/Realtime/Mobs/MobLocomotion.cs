using Mmorpg.GameSimulation;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Realtime;

// The movement settings and route planner of one locomotion profile. A Mob walks with
// the same motor and collision as players, at its profile's speed and with its capsule;
// only the speed and the capsule size differ from the player settings. It chases and
// walks home at its walk speed and follows its patrol at its slower patrol speed.
// Loop-confined, because the planner is not thread-safe.
internal sealed class MobLocomotion
{
    // Each route may expand this many grid cells, which covers the open ground inside a
    // leash range; a blocked search returns the closest reachable point instead.
    public const int MaximumExpandedNodes = 4096;

    public MobLocomotion(
        WorldActorLocomotionProfileDefinition profile,
        MovementSimulationSettings world,
        ICollisionWorld collisionWorld,
        float searchDistance)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(collisionWorld);
        var player = world.CharacterCollision;
        Settings = SettingsAt(profile.WalkSpeed);
        PatrolSettings = SettingsAt(profile.PatrolSpeed);

        var distance = Math.Clamp(searchDistance, 4f, MovementPathRules.MaximumLength);
        Planner = new MovementPathPlanner(
            collisionWorld,
            Settings,
            new MovementPathPlannerSettings(
                0.5f,
                distance,
                MaximumExpandedNodes,
                0.05f,
                Math.Min(MovementPathRules.MaximumLength, distance * 3f)));

        MovementSimulationSettings SettingsAt(float speed) => new(
            world.TickRateHz,
            speed,
            world.RotationSpeedDegrees,
            world.Gravity,
            world.MaximumFallSpeed,
            world.GroundedVerticalVelocity,
            world.GroundHeight,
            world.MinimumX,
            world.MaximumX,
            world.MinimumZ,
            world.MaximumZ,
            new CharacterCollisionSettings(
                profile.CollisionRadius,
                profile.CollisionHeight,
                Math.Min(player.StepHeight, profile.CollisionHeight * 0.5f),
                player.MaximumSlopeDegrees,
                player.GroundSnapDistance,
                player.MaximumSubstepDistance,
                player.MaximumPenetrationIterations));
    }

    public MovementSimulationSettings Settings { get; }

    public MovementSimulationSettings PatrolSettings { get; }

    public MovementPathPlanner Planner { get; }

    public float Height => Settings.CharacterCollision.Height;
}
