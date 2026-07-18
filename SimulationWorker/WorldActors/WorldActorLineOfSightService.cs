using ShooterMmo.GameSimulation;
using ShooterMmo.WorldData.Actors;

namespace SimulationWorker.WorldActors;

public sealed class WorldActorLineOfSightService(ICollisionWorld collisionWorld)
{
    private readonly CollisionQueryBuffer queryBuffer = new();

    public bool HasLineOfSight(
        float playerX,
        float playerY,
        float playerZ,
        WorldActorRuntimeState actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var origin = new SimulationVector3(
            playerX,
            playerY + WorldInteractionRules.AuthoritativeEyeHeight,
            playerZ);
        var target = new SimulationVector3(
            actor.BoundsCenterX,
            actor.BoundsCenterY,
            actor.BoundsCenterZ);
        var direction = target - origin;
        var distance = direction.Length;
        if (distance <= 0.001f)
        {
            return true;
        }

        var minimum = SimulationVector3.Min(origin, target);
        var maximum = SimulationVector3.Max(origin, target);
        var bounds = new CollisionAabb(minimum, maximum).Expanded(0.01f);
        queryBuffer.Clear();
        collisionWorld.QueryBoxes(bounds, CollisionLayers.CombatQueries, queryBuffer);
        foreach (var box in queryBuffer.Boxes)
        {
            if (CollisionMath.TryRaycastBox(
                    origin,
                    direction,
                    distance,
                    box,
                    out var hit)
                && hit.Distance < distance - 0.05f)
            {
                return false;
            }
        }

        return true;
    }
}
