using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Realtime;

// Runs the Mob brains and their movement after world actor activity and before combat.
// An active Mob thinks at its activity profile's rate, staggered by entity id, and a
// moving Mob steps every tick with the shared motor. A dormant Mob does nothing unless
// it is still fighting or on its way home. Routes come from one planning queue with a
// fixed budget per tick. Combat owns damage and the attack itself; the brain chooses the
// target, walks, and decides when the Mob stands ready to attack. Loop-confined.
internal sealed class MobSystem
{
    // Routes planned per tick at most, shared by every Mob.
    public const int PlansPerTick = 8;

    // A chasing Mob plans again when its target moved this far from the planned goal.
    public const float ReplanDistance = 1.5f;

    // A Mob counts as arrived within this planar distance.
    public const float ArrivalDistance = 0.5f;

    // An evading Mob that cannot walk home after this many routes is placed home.
    public const int MaximumFailedPlans = 3;

    // A chasing Mob walks to this share of its attack range from the target.
    private const float ChaseDistanceShare = 0.5f;

    // The eye of a Mob is at this share of its capsule height.
    private const float EyeHeightShare = 0.8f;

    // The ground under a spawn is searched this far above and below its authored height.
    private const float GroundSearchHeight = 20f;

    private readonly SimulationWorkerConfig config;
    private readonly RealtimeSimulationClock clock;
    private readonly ICollisionWorld collisionWorld;
    private readonly CombatantRegistry combatants;
    private readonly CombatLineOfSight lineOfSight;
    private readonly EntityPresenceReplicator presence;
    private readonly CombatMetrics? metrics;
    private readonly Dictionary<string, WorldActorBrainProfileDefinition> brains;
    private readonly Dictionary<string, WorldActorLocomotionProfileDefinition> locomotionProfiles;
    private readonly Dictionary<string, MobLocomotion> locomotions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorldActorPatrolPointDefinition[]> patrolPaths;
    private readonly Dictionary<string, WorldActorActivityProfileDefinition> activityProfiles;
    private readonly Dictionary<ulong, MobAgent> agents = [];
    private readonly List<MobAgent> ordered = [];
    private readonly MobPathPlanningQueue planning = new();
    private readonly CollisionQueryBuffer queryBuffer = new();
    private readonly float searchDistance;
    private readonly Action<MobAgent, MovementPathPlan> applyPlan;
    private readonly Func<PlayerSimulationEntity, bool> isDetectable;
    private SimulationVector3 detectingEye;

    public MobSystem(
        SimulationWorkerConfig config,
        RealtimeSimulationClock clock,
        ICollisionWorld collisionWorld,
        CombatantRegistry combatants,
        CombatLineOfSight lineOfSight,
        EntityPresenceReplicator presence,
        WorldActorRuntimeDocument? content,
        CombatMetrics? metrics)
    {
        this.config = config;
        this.clock = clock;
        this.collisionWorld = collisionWorld;
        this.combatants = combatants;
        this.lineOfSight = lineOfSight;
        this.presence = presence;
        this.metrics = metrics;
        brains = (content?.BrainProfiles ?? []).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        locomotionProfiles = (content?.LocomotionProfiles ?? []).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        patrolPaths = (content?.PatrolPaths ?? []).ToDictionary(
            path => path.Id,
            path => path.Points ?? [],
            StringComparer.Ordinal);
        activityProfiles = (content?.ActivityProfiles ?? []).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        // A route covers the leash and the detection range around it.
        searchDistance = brains.Count == 0
            ? 32f
            : brains.Values.Max(brain => brain.LeashRange + brain.DetectionRange) + 8f;
        applyPlan = ApplyPlan;
        isDetectable = IsDetectable;
    }

    public int Count => ordered.Count;

    public int QueuedPlans => planning.Count;

    // A Mob with a brain and a locomotion profile thinks and walks; any other damageable
    // Mob stays a stationary target.
    public void Register(Combatant mob)
    {
        ArgumentNullException.ThrowIfNull(mob);
        var actor = mob.Actor;
        if (actor is null
            || agents.ContainsKey(mob.EntityId)
            || string.IsNullOrEmpty(actor.Definition.BrainProfileId)
            || !brains.TryGetValue(actor.Definition.BrainProfileId, out var brain)
            || !TryGetLocomotion(actor.Definition.LocomotionProfileId, out var locomotion))
        {
            return;
        }

        var patrol = !string.IsNullOrEmpty(actor.Spawn.PatrolPathId)
            && patrolPaths.TryGetValue(actor.Spawn.PatrolPathId, out var points)
                ? points
                : [];
        var rate = activityProfiles.TryGetValue(actor.Definition.ActivityProfileId, out var activity)
            ? activity.ActiveTickRateHz
            : 10f;
        var interval = (uint)Math.Max(1, (int)MathF.Round(config.MovementSimulation.TickRateHz / Math.Max(0.1f, rate)));
        var agent = new MobAgent(mob, locomotion, brain, patrol, interval);
        agents.Add(mob.EntityId, agent);
        var index = ordered.BinarySearch(agent, AgentOrder.Instance);
        ordered.Insert(index < 0 ? ~index : index, agent);
    }

    public void Remove(ulong entityId)
    {
        if (!agents.Remove(entityId, out var agent))
        {
            return;
        }

        agent.Removed = true;
        var index = ordered.BinarySearch(agent, AgentOrder.Instance);
        if (index >= 0)
        {
            ordered.RemoveAt(index);
        }
    }

    // A Mob that fights, walks home, or died stays active, whoever is near.
    public bool IsBusy(WorldActorRuntimeState actor)
    {
        return agents.TryGetValue(actor.NetworkEntityId, out var agent)
            && (agent.IsEngaged || agent.State == MobBrainState.Dead);
    }

    // The brain stands in attack range and waits for its cooldown; combat checks range,
    // line of sight, and the cooldown again before every attack.
    public bool IsReadyToAttack(Combatant mob)
    {
        return agents.TryGetValue(mob.EntityId, out var agent)
            && agent.State == MobBrainState.Attack
            && !agent.IsFollowingPath;
    }

    public SimulationVector3 EyeOf(Combatant mob)
    {
        var actor = mob.Actor!;
        var height = agents.TryGetValue(mob.EntityId, out var agent)
            ? agent.Locomotion.Height
            : actor.Definition.InteractionBounds.SizeY;
        return new SimulationVector3(actor.PositionX, actor.PositionY + (height * EyeHeightShare), actor.PositionZ);
    }

    // The Mob attacked or took a hit, which keeps its give-up time from running out.
    public void OnEngaged(ulong entityId, uint serverTick)
    {
        if (agents.TryGetValue(entityId, out var agent))
        {
            agent.LastEngagementTick = serverTick;
        }
    }

    public void ProcessTick(PlayerSpatialGrid players)
    {
        ArgumentNullException.ThrowIfNull(players);
        var started = Stopwatch.GetTimestamp();
        var serverTick = clock.ServerTick;
        foreach (var agent in ordered)
        {
            agent.PoseChangedThisTick = false;
            if (!ShouldRun(agent) || (!agent.HasMovement && !TryStartMovement(agent)))
            {
                continue;
            }

            if (agent.Combatant.LifeState == RealtimeCombatLifeState.Dead)
            {
                EnterDead(agent);
                continue;
            }

            if ((serverTick + (uint)agent.EntityId) % agent.ThinkIntervalTicks == 0)
            {
                Think(agent, players, serverTick);
            }
        }

        planning.Process(PlansPerTick, applyPlan);
        var engaged = 0;
        var moving = 0;
        foreach (var agent in ordered)
        {
            if (agent.HasMovement && ShouldRun(agent))
            {
                Move(agent, serverTick);
            }

            PublishRestPose(agent);
            engaged += agent.IsEngaged ? 1 : 0;
            moving += agent.PoseChangedThisTick ? 1 : 0;
        }

        metrics?.SetMobActivity(engaged, moving, planning.Count);
        metrics?.RecordMobPhase(Stopwatch.GetElapsedTime(started));
    }

    // The newest pose of every Mob that moved since the last movement snapshot.
    public void CopyPendingSnapshotsTo(List<RealtimeEntitySnapshot> destination)
    {
        destination.Clear();
        foreach (var agent in ordered)
        {
            if (!agent.SnapshotPending)
            {
                continue;
            }

            agent.SnapshotPending = false;
            destination.Add(new RealtimeEntitySnapshot(
                agent.EntityId,
                0,
                RealtimeStateMapper.ToRealtimePlayerState(agent.Movement)));
        }
    }

    // Walking Mobs keep the collision under them loaded, like players do.
    public void CopyCollisionAnchorsTo(List<SimulationVector3> destination)
    {
        foreach (var agent in ordered)
        {
            if (agent.IsFollowingPath || agent.IsEngaged || (!agent.HasMovement && ShouldRun(agent)))
            {
                destination.Add(agent.Position);
            }
        }
    }

    public void Clear()
    {
        planning.Clear();
        foreach (var agent in ordered)
        {
            agent.Removed = true;
        }

        agents.Clear();
        ordered.Clear();
    }

    private bool ShouldRun(MobAgent agent)
    {
        return agent.IsEngaged
            || string.Equals(agent.Actor.ActivityTier, WorldActorActivityTierIds.Active, StringComparison.Ordinal);
    }

    // The Mob starts on the ground under its spawn once that collision is loaded. The
    // authored height may be off the terrain, so the ground is searched from above.
    private bool TryStartMovement(MobAgent agent)
    {
        var actor = agent.Actor;
        var ground = GroundUnder(new SimulationVector3(actor.PositionX, actor.PositionY, actor.PositionZ));
        var state = PlayerMovementSimulation.CreateInitialState(
            agent.Locomotion.Settings,
            collisionWorld,
            ground.X,
            ground.Y,
            ground.Z,
            actor.YawDegrees);
        if (!state.IsGrounded)
        {
            return false;
        }

        agent.HasMovement = true;
        agent.Home = state.Position;
        SetMovement(agent, state);
        return true;
    }

    private void Think(MobAgent agent, PlayerSpatialGrid players, uint serverTick)
    {
        switch (agent.State)
        {
            case MobBrainState.Idle:
            case MobBrainState.Patrol:
                if (TrySelectTarget(agent, players, out var target))
                {
                    Engage(agent, target, serverTick);
                    Fight(agent, players, serverTick);
                    return;
                }

                FollowRoutine(agent, serverTick);
                return;
            case MobBrainState.Chase:
            case MobBrainState.Attack:
                Fight(agent, players, serverTick);
                return;
            case MobBrainState.Evade:
                WalkHome(agent, serverTick);
                return;
        }
    }

    // The attacker that dealt the most damage and is still valid, then the nearest player
    // an aggressive brain detects in range with line of sight. Damage always starts a
    // fight, whatever the brain's aggression.
    private bool TrySelectTarget(MobAgent agent, PlayerSpatialGrid players, out ulong target)
    {
        target = 0;
        var bestDamage = long.MinValue;
        foreach (var (attacker, damage) in agent.Combatant.DamageLedger)
        {
            if (!TryGetLivingPlayer(attacker, out _)
                || damage < bestDamage
                || (damage == bestDamage && attacker > target))
            {
                continue;
            }

            target = attacker;
            bestDamage = damage;
        }

        if (target != 0)
        {
            return true;
        }

        if (!string.Equals(agent.Brain.Aggression, WorldActorAggressionIds.Aggressive, StringComparison.Ordinal))
        {
            return false;
        }

        var position = agent.Position;
        detectingEye = EyeOf(agent.Combatant);
        var detected = players.FindNearest(
            position.X,
            position.Z,
            agent.Brain.DetectionRange,
            isDetectable);
        target = detected?.NetworkEntityId ?? 0;
        return target != 0;
    }

    // One cached filter for detection, so thinking does not allocate.
    private bool IsDetectable(PlayerSimulationEntity player)
    {
        return TryGetLivingPlayer(player.NetworkEntityId, out var candidate)
            && lineOfSight.HasLineOfSight(detectingEye, BoundsOf(candidate).Center);
    }

    private void Engage(MobAgent agent, ulong target, uint serverTick)
    {
        if (!agent.IsEngaged)
        {
            agent.Home = agent.Movement.Position;
        }

        agent.State = MobBrainState.Chase;
        agent.LastEngagementTick = serverTick;
        agent.WaitUntilTick = 0;
        agent.FailedPlans = 0;
        SetTarget(agent, target);
    }

    private void Fight(MobAgent agent, PlayerSpatialGrid players, uint serverTick)
    {
        var mob = agent.Combatant;
        if (!TryGetLivingPlayer(mob.TargetEntityId, out var target))
        {
            if (!TrySelectTarget(agent, players, out var next))
            {
                BeginEvade(agent);
                return;
            }

            SetTarget(agent, next);
            target = combatants.TryGet(next, out var selected) ? selected : null!;
        }

        var giveUpTicks = CombatTicks.FromSeconds(agent.Brain.GiveUpSeconds, config.MovementSimulation.TickRateHz);
        if (PlanarDistance(agent.Movement.Position, agent.Home) > agent.Brain.LeashRange
            || CombatTicks.IsReached(serverTick, agent.LastEngagementTick + Math.Max(1u, giveUpTicks)))
        {
            BeginEvade(agent);
            return;
        }

        var targetPosition = target.Player!.Movement.State.Position;
        var range = mob.MobAttack!.Range;
        var bounds = BoundsOf(target);
        if (bounds.DistanceFrom(agent.Movement.Position) <= range
            && lineOfSight.HasLineOfSight(EyeOf(mob), bounds.Center))
        {
            agent.State = MobBrainState.Attack;
            StopPath(agent);
            Face(agent, targetPosition);
            return;
        }

        agent.State = MobBrainState.Chase;
        var destination = ApproachPoint(agent.Movement.Position, targetPosition, range * ChaseDistanceShare);
        if ((!agent.IsFollowingPath && !agent.PlanQueued)
            || PlanarDistance(agent.IsFollowingPath ? agent.PathGoal : agent.PlanDestination, destination) > ReplanDistance)
        {
            planning.Request(agent, destination);
        }
    }

    // The Mob turns away, cannot be damaged, and walks back to where it started to fight.
    private void BeginEvade(MobAgent agent)
    {
        var mob = agent.Combatant;
        agent.State = MobBrainState.Evade;
        agent.FailedPlans = 0;
        mob.TargetEntityId = 0;
        mob.LifeState = RealtimeCombatLifeState.Evading;
        mob.MarkStateChanged();
        StopPath(agent);
        planning.Request(agent, agent.Home);
    }

    private void WalkHome(MobAgent agent, uint serverTick)
    {
        if (agent.IsFollowingPath || agent.PlanQueued)
        {
            return;
        }

        if (PlanarDistance(agent.Movement.Position, agent.Home) <= ArrivalDistance)
        {
            ArriveHome(agent, serverTick);
            return;
        }

        if (agent.FailedPlans >= MaximumFailedPlans)
        {
            // Home cannot be reached on foot, so the Mob is placed there.
            var home = agent.Home;
            SetMovement(agent, PlayerMovementSimulation.CreateInitialState(
                agent.Locomotion.Settings,
                collisionWorld,
                home.X,
                home.Y,
                home.Z,
                agent.Movement.YawDegrees));
            ArriveHome(agent, serverTick);
            return;
        }

        planning.Request(agent, agent.Home);
    }

    // Home again: full health, out of combat, and back to the routine.
    private void ArriveHome(MobAgent agent, uint serverTick)
    {
        var mob = agent.Combatant;
        mob.Health = mob.MaximumHealth;
        mob.LifeState = RealtimeCombatLifeState.Alive;
        mob.InCombat = false;
        mob.InCombatUntilTick = serverTick;
        mob.TargetEntityId = 0;
        mob.NextAttackTick = 0;
        mob.DamageLedger.Clear();
        mob.MarkStateChanged();
        agent.State = agent.Patrol.Count > 0 ? MobBrainState.Patrol : MobBrainState.Idle;
        agent.WaitUntilTick = 0;
        agent.FailedPlans = 0;
    }

    // A patrolling Mob walks from point to point and waits at each; an idle Mob stands.
    private void FollowRoutine(MobAgent agent, uint serverTick)
    {
        if (agent.State != MobBrainState.Patrol
            || agent.Patrol.Count == 0
            || agent.IsFollowingPath
            || agent.PlanQueued)
        {
            return;
        }

        if (agent.WaitUntilTick != 0)
        {
            if (!CombatTicks.IsReached(serverTick, agent.WaitUntilTick))
            {
                return;
            }

            agent.WaitUntilTick = 0;
            agent.PatrolIndex = (agent.PatrolIndex + 1) % agent.Patrol.Count;
        }

        var point = agent.Patrol[agent.PatrolIndex];
        var destination = GroundUnder(new SimulationVector3(point.X, point.Y, point.Z));
        if (PlanarDistance(agent.Movement.Position, destination) <= ArrivalDistance
            || agent.FailedPlans >= MaximumFailedPlans)
        {
            // Arrived, or the point cannot be reached; either way the Mob waits there.
            agent.FailedPlans = 0;
            agent.WaitUntilTick = serverTick + Math.Max(
                1u,
                CombatTicks.FromSeconds(point.WaitSeconds, config.MovementSimulation.TickRateHz));
            return;
        }

        planning.Request(agent, destination);
    }

    private void EnterDead(MobAgent agent)
    {
        if (agent.State == MobBrainState.Dead)
        {
            return;
        }

        agent.State = MobBrainState.Dead;
        StopPath(agent);
    }

    private void ApplyPlan(MobAgent agent, MovementPathPlan plan)
    {
        metrics?.RecordMobPathPlan(plan.Status switch
        {
            MovementPathPlanStatus.Reached => "reached",
            MovementPathPlanStatus.Closest => "closest",
            _ => "no_path"
        });
        if (agent.State == MobBrainState.Dead
            || plan.Status == MovementPathPlanStatus.NoPath
            || plan.Waypoints.Count == 0)
        {
            agent.FailedPlans++;
            agent.Path = null;
            return;
        }

        agent.Path = new MovementPath(agent.NextPathId, plan.Waypoints);
        agent.NextPathId = agent.NextPathId == uint.MaxValue ? 1 : agent.NextPathId + 1;
        agent.PathGoal = agent.PlanDestination;
    }

    private void Move(MobAgent agent, uint serverTick)
    {
        var path = agent.Path;
        if (path is null)
        {
            return;
        }

        // A patrolling Mob walks; a Mob in a fight or on its way home runs.
        var next = PlayerMovementSimulation.Step(
            agent.Movement,
            new PlayerMovementInput(0, serverTick, path),
            agent.State == MobBrainState.Patrol ? agent.Locomotion.PatrolSettings : agent.Locomotion.Settings,
            PlayerCarryState.Default,
            collisionWorld,
            queryBuffer);
        SetMovement(agent, next);
        if (next.PathProgress.PathId != path.Id || next.PathProgress.NextWaypointIndex < path.Count)
        {
            return;
        }

        // The motor ends a blocked path early, and a route to the closest point ends
        // short of the goal; both count as a failed route.
        var reachedGoal = PlanarDistance(next.Position, agent.PathGoal) <= ArrivalDistance;
        agent.FailedPlans = reachedGoal ? 0 : agent.FailedPlans + 1;
        StopPath(agent);
    }

    // Movement snapshots carry a walking Mob; the first tick it stands still sends one
    // reliable state with its rest pose.
    private void PublishRestPose(MobAgent agent)
    {
        if (agent.PoseChangedThisTick || agent.RestPublished)
        {
            return;
        }

        agent.RestPublished = true;
        agent.SnapshotPending = false;
        agent.Actor.PublishRestPose();
        presence.BroadcastWorldActorState(agent.Actor);
    }

    private void SetMovement(MobAgent agent, PlayerMovementState state)
    {
        var previous = agent.Movement;
        agent.Movement = state;
        if (agent.HasMovement
            && previous.PositionX == state.PositionX
            && previous.PositionY == state.PositionY
            && previous.PositionZ == state.PositionZ
            && previous.YawDegrees == state.YawDegrees
            && agent.Actor.PositionY == state.PositionY)
        {
            return;
        }

        agent.Actor.SetPose(state.PositionX, state.PositionY, state.PositionZ, state.YawDegrees);
        agent.PoseChangedThisTick = true;
        agent.SnapshotPending = true;
        agent.RestPublished = false;
    }

    private void StopPath(MobAgent agent)
    {
        agent.Path = null;
        agent.PlanQueued = false;
        var state = agent.Movement;
        if (state.VelocityX == 0f && state.VelocityZ == 0f)
        {
            return;
        }

        SetMovement(agent, new PlayerMovementState(
            state.PositionX,
            state.PositionY,
            state.PositionZ,
            0f,
            state.VelocityY,
            0f,
            state.YawDegrees,
            state.IsGrounded,
            MovementPathProgress.None));
    }

    private void Face(MobAgent agent, SimulationVector3 target)
    {
        var state = agent.Movement;
        var deltaX = target.X - state.PositionX;
        var deltaZ = target.Z - state.PositionZ;
        if ((deltaX * deltaX) + (deltaZ * deltaZ) < 0.0001f)
        {
            return;
        }

        var yaw = (float)(Math.Atan2(deltaX, deltaZ) * (180d / Math.PI));
        yaw = yaw < 0f ? yaw + 360f : yaw;
        if (Math.Abs(yaw - state.YawDegrees) < 1f)
        {
            return;
        }

        SetMovement(agent, new PlayerMovementState(
            state.PositionX,
            state.PositionY,
            state.PositionZ,
            state.VelocityX,
            state.VelocityY,
            state.VelocityZ,
            yaw,
            state.IsGrounded,
            state.PathProgress));
    }

    private void SetTarget(MobAgent agent, ulong target)
    {
        var mob = agent.Combatant;
        if (mob.TargetEntityId == target)
        {
            return;
        }

        mob.TargetEntityId = target;
        mob.MarkStateChanged();
    }

    // The first walkable surface below a point searched from above it, or the point itself
    // when nothing is loaded there. Authored heights may be off the terrain.
    private SimulationVector3 GroundUnder(SimulationVector3 point)
    {
        return CollisionRaycast.TryRaycast(
                collisionWorld,
                new SimulationVector3(point.X, point.Y + GroundSearchHeight, point.Z),
                new SimulationVector3(0f, -1f, 0f),
                GroundSearchHeight * 2f,
                CollisionLayers.CharacterMovement,
                queryBuffer,
                out var hit)
            ? new SimulationVector3(point.X, hit.Point.Y, point.Z)
            : point;
    }

    private bool TryGetLocomotion(string profileId, out MobLocomotion locomotion)
    {
        if (locomotions.TryGetValue(profileId ?? string.Empty, out locomotion!))
        {
            return true;
        }

        if (string.IsNullOrEmpty(profileId) || !locomotionProfiles.TryGetValue(profileId, out var profile))
        {
            return false;
        }

        locomotion = new MobLocomotion(profile, config.MovementSimulation, collisionWorld, searchDistance);
        locomotions.Add(profileId, locomotion);
        return true;
    }

    private bool TryGetLivingPlayer(ulong entityId, out Combatant player)
    {
        return combatants.TryGet(entityId, out player)
            && player.Kind == CombatantKind.Player
            && player.IsAlive;
    }

    private CombatBounds BoundsOf(Combatant player)
    {
        var collision = config.MovementSimulation.CharacterCollision;
        return CombatBounds.Capsule(player.Player!.Movement.State.Position, collision.Radius, collision.Height);
    }

    // The point at the given distance from the target on the line toward the Mob.
    private static SimulationVector3 ApproachPoint(SimulationVector3 from, SimulationVector3 target, float distance)
    {
        var deltaX = from.X - target.X;
        var deltaZ = from.Z - target.Z;
        var length = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (length <= distance || length < 0.0001f)
        {
            return target;
        }

        var scale = distance / length;
        return new SimulationVector3(target.X + (deltaX * scale), target.Y, target.Z + (deltaZ * scale));
    }

    private static float PlanarDistance(SimulationVector3 first, SimulationVector3 second)
    {
        var deltaX = first.X - second.X;
        var deltaZ = first.Z - second.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private sealed class AgentOrder : IComparer<MobAgent>
    {
        public static readonly AgentOrder Instance = new();

        public int Compare(MobAgent? left, MobAgent? right)
        {
            return left!.EntityId.CompareTo(right!.EntityId);
        }
    }
}
