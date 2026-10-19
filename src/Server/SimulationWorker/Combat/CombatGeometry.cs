using Mmorpg.GameSimulation;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Combat;

// The combat bounds of a combatant: a vertical capsule for a character, the interaction
// box for a world actor. Range is measured from the attacker's root to the closest
// point of these bounds, and line of sight aims at their center.
public readonly record struct CombatBounds(
    bool IsCapsule,
    SimulationVector3 Root,
    float CapsuleRadius,
    float CapsuleHeight,
    SimulationVector3 BoxCenter,
    SimulationVector3 BoxSize)
{
    public static CombatBounds Capsule(SimulationVector3 root, float radius, float height)
    {
        return new CombatBounds(true, root, radius, height, default, default);
    }

    public static CombatBounds Box(SimulationVector3 root, SimulationVector3 center, SimulationVector3 size)
    {
        return new CombatBounds(false, root, 0f, 0f, center, size);
    }

    public SimulationVector3 Center => IsCapsule
        ? new SimulationVector3(Root.X, Root.Y + (CapsuleHeight * 0.5f), Root.Z)
        : BoxCenter;

    public float DistanceFrom(SimulationVector3 point)
    {
        if (!IsCapsule)
        {
            return MathF.Sqrt(WorldInteractionRules.DistanceSquaredToBounds(
                point.X,
                point.Y,
                point.Z,
                BoxCenter.X,
                BoxCenter.Y,
                BoxCenter.Z,
                BoxSize.X,
                BoxSize.Y,
                BoxSize.Z));
        }

        var bottom = Root.Y + CapsuleRadius;
        var top = Root.Y + Math.Max(CapsuleRadius, CapsuleHeight - CapsuleRadius);
        var closestY = Math.Clamp(point.Y, bottom, top);
        var deltaX = point.X - Root.X;
        var deltaY = point.Y - closestY;
        var deltaZ = point.Z - Root.Z;
        var distance = MathF.Sqrt((deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ));
        return Math.Max(0f, distance - CapsuleRadius);
    }
}

// One line of sight rule for every combatant: a ray through the CombatQueries layer
// from the attacker's eye to the target's bounds center. Loop-confined.
public sealed class CombatLineOfSight(ICollisionWorld collisionWorld)
{
    private readonly CollisionQueryBuffer queryBuffer = new();

    public static SimulationVector3 EyeOf(SimulationVector3 root)
    {
        return new SimulationVector3(root.X, root.Y + WorldInteractionRules.AuthoritativeEyeHeight, root.Z);
    }

    public bool HasLineOfSight(SimulationVector3 origin, SimulationVector3 target)
    {
        var direction = target - origin;
        var distance = direction.Length;
        if (distance <= 0.001f)
        {
            return true;
        }

        var bounds = new CollisionAabb(
            SimulationVector3.Min(origin, target),
            SimulationVector3.Max(origin, target)).Expanded(0.01f);
        queryBuffer.Clear();
        collisionWorld.QueryShapes(bounds, CollisionLayers.CombatQueries, queryBuffer);
        foreach (var shape in queryBuffer.Shapes)
        {
            if (shape.TryRaycast(origin, direction, distance, out var hit)
                && hit.Distance < distance - 0.05f)
            {
                return false;
            }
        }

        return true;
    }
}
