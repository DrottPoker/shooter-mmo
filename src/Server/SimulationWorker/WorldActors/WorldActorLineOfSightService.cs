using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;

namespace Mmorpg.SimulationWorker.WorldActors;

// Line of sight from a character to a world actor, with the same rule as combat.
public sealed class WorldActorLineOfSightService(ICollisionWorld collisionWorld)
{
    private readonly CombatLineOfSight lineOfSight = new(collisionWorld);

    public bool HasLineOfSight(
        float playerX,
        float playerY,
        float playerZ,
        WorldActorRuntimeState actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return lineOfSight.HasLineOfSight(
            CombatLineOfSight.EyeOf(new SimulationVector3(playerX, playerY, playerZ)),
            new SimulationVector3(actor.BoundsCenterX, actor.BoundsCenterY, actor.BoundsCenterZ));
    }
}
