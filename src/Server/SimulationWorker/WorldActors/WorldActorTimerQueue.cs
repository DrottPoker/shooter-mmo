namespace Mmorpg.SimulationWorker.WorldActors;

// One due world actor timer, such as the respawn of a depleted resource node. The
// generation makes a timer from an older cycle of the actor a no-op.
public enum WorldActorTimerKind
{
    // A depleted resource node returns with full charges.
    ResourceReplenish,

    // A dead Mob's spawn instance spawns a new Mob.
    MobRespawn
}

public readonly record struct WorldActorTimer(
    Guid RuntimeActorId,
    long Generation,
    WorldActorTimerKind Kind = WorldActorTimerKind.ResourceReplenish);

// The central due-tick queue for world actor timers. No actor owns a task or timer; the
// tick loop drains whatever is due. Each actor has at most one pending timer per cycle,
// so the queue stays bounded by the actor population. Loop-confined.
public sealed class WorldActorTimerQueue
{
    private readonly PriorityQueue<WorldActorTimer, (uint DueTick, long Sequence)> timers = new();
    private long sequence;

    public int Count => timers.Count;

    public void Schedule(
        uint dueTick,
        Guid runtimeActorId,
        long generation,
        WorldActorTimerKind kind = WorldActorTimerKind.ResourceReplenish)
    {
        if (runtimeActorId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeActorId));
        }

        timers.Enqueue(new WorldActorTimer(runtimeActorId, generation, kind), (dueTick, sequence++));
    }

    // Moves every timer due at or before the tick into the buffer, in due order.
    public void DrainDue(uint serverTick, List<WorldActorTimer> due)
    {
        ArgumentNullException.ThrowIfNull(due);
        due.Clear();
        while (timers.TryPeek(out var timer, out var priority) && priority.DueTick <= serverTick)
        {
            timers.Dequeue();
            due.Add(timer);
        }
    }

    public void Clear()
    {
        timers.Clear();
    }
}
