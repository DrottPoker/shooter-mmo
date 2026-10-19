using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Realtime;

internal enum MobBrainState
{
    // Stands at its spawn.
    Idle,

    // Walks its patrol path with the authored waits.
    Patrol,

    // Walks toward its target until it stands in attack range with line of sight.
    Chase,

    // Stands still and attacks on its cooldown.
    Attack,

    // Walks home invulnerable and heals fully on arrival.
    Evade,

    // Waits for its corpse and despawn.
    Dead
}

// The brain and movement of one Mob. Loop-confined; only the Mob system changes it.
internal sealed class MobAgent(
    Combatant combatant,
    MobLocomotion locomotion,
    WorldActorBrainProfileDefinition brain,
    IReadOnlyList<WorldActorPatrolPointDefinition> patrol,
    uint thinkIntervalTicks)
{
    public Combatant Combatant { get; } = combatant;

    public WorldActorRuntimeState Actor { get; } = combatant.Actor!;

    public ulong EntityId => Combatant.EntityId;

    public MobLocomotion Locomotion { get; } = locomotion;

    public WorldActorBrainProfileDefinition Brain { get; } = brain;

    public IReadOnlyList<WorldActorPatrolPointDefinition> Patrol { get; } = patrol;

    // The brain thinks on ticks where the tick plus the entity id is a multiple of this,
    // so the Mobs of one profile spread over the ticks.
    public uint ThinkIntervalTicks { get; } = Math.Max(1u, thinkIntervalTicks);

    public MobBrainState State { get; set; } = patrol.Count > 0 ? MobBrainState.Patrol : MobBrainState.Idle;

    // False until the Mob first stood on loaded ground; it does not move before.
    public bool HasMovement { get; set; }

    public PlayerMovementState Movement { get; set; }

    public MovementPath? Path { get; set; }

    public uint NextPathId { get; set; } = 1;

    // The destination of the current path.
    public SimulationVector3 PathGoal { get; set; }

    // A route request waits in the planning queue for this destination.
    public bool PlanQueued { get; set; }

    public SimulationVector3 PlanDestination { get; set; }

    // Routes in a row that found no way; the evade gives up walking after a few.
    public int FailedPlans { get; set; }

    // Where the Mob left its routine to fight: the leash and the evade measure from here.
    public SimulationVector3 Home { get; set; }

    public int PatrolIndex { get; set; }

    // A patrol stop lasts until this tick; 0 means the Mob is not waiting.
    public uint WaitUntilTick { get; set; }

    // The last tick the Mob attacked or was hit; the brain gives up after its give-up time.
    public uint LastEngagementTick { get; set; }

    // The pose changed since the last movement snapshot that carried the Mob.
    public bool SnapshotPending { get; set; }

    // The pose changed this tick.
    public bool PoseChangedThisTick { get; set; }

    // The current rest pose has been sent reliably.
    public bool RestPublished { get; set; } = true;

    public bool Removed { get; set; }

    public bool IsFollowingPath => Path is not null;

    public bool IsEngaged => State is MobBrainState.Chase or MobBrainState.Attack or MobBrainState.Evade;

    public SimulationVector3 Position => new(Actor.PositionX, Actor.PositionY, Actor.PositionZ);
}
