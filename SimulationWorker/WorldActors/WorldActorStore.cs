using ShooterMmo.WorldData.Actors;
using SimulationWorker.Entities;

namespace SimulationWorker.WorldActors;

public sealed class WorldActorRuntimeState
{
    internal WorldActorRuntimeState(
        ulong networkEntityId,
        Guid runtimeActorId,
        string worldId,
        string shardId,
        string workerRuntimeId,
        WorldActorDefinition definition,
        WorldActorSpawnInstanceDefinition spawn)
    {
        NetworkEntityId = networkEntityId;
        RuntimeActorId = runtimeActorId;
        WorldId = worldId;
        ShardId = shardId;
        WorkerRuntimeId = workerRuntimeId;
        Definition = definition;
        Spawn = spawn;
        PositionX = spawn.X;
        PositionY = spawn.Y;
        PositionZ = spawn.Z;
        YawDegrees = spawn.YawDegrees;
        IsActive = true;
        StateRevision = 1;
        InteractionRevision = 1;
        ActivityTier = string.Equals(
            definition.Kind,
            WorldActorKindIds.Npc,
            StringComparison.Ordinal)
            ? WorldActorActivityTierIds.EventDriven
            : WorldActorActivityTierIds.Dormant;
    }

    public ulong NetworkEntityId { get; }
    public Guid RuntimeActorId { get; }
    public string WorldId { get; }
    public string ShardId { get; }
    public string WorkerRuntimeId { get; }
    public WorldActorDefinition Definition { get; }
    public WorldActorSpawnInstanceDefinition Spawn { get; }
    public float PositionX { get; private set; }
    public float PositionY { get; private set; }
    public float PositionZ { get; private set; }
    public float YawDegrees { get; private set; }
    public bool IsActive { get; private set; }
    public long StateRevision { get; private set; }
    public long InteractionRevision { get; private set; }
    public string ActivityTier { get; private set; }

    public float BoundsCenterX => PositionX + Definition.InteractionBounds.CenterX;
    public float BoundsCenterY => PositionY + Definition.InteractionBounds.CenterY;
    public float BoundsCenterZ => PositionZ + Definition.InteractionBounds.CenterZ;

    internal bool SetActivityTier(string activityTier)
    {
        if (string.Equals(ActivityTier, activityTier, StringComparison.Ordinal))
        {
            return false;
        }

        ActivityTier = activityTier;
        StateRevision++;
        return true;
    }

    internal bool SetActive(bool isActive)
    {
        if (IsActive == isActive)
        {
            return false;
        }

        IsActive = isActive;
        StateRevision++;
        InteractionRevision++;
        return true;
    }

    internal void InvalidateInteractionRevision()
    {
        InteractionRevision++;
        StateRevision++;
    }
}

public sealed class WorldActorStore
{
    private readonly object syncRoot = new();
    private readonly WorldActorRuntimeDocument document;
    private readonly SimulationEntityRegistry entityRegistry;
    private readonly Func<Guid> runtimeIdFactory;
    private readonly Dictionary<ulong, WorldActorRuntimeState> actorsByEntityId = [];
    private readonly Dictionary<Guid, WorldActorRuntimeState> actorsByRuntimeId = [];
    private readonly Dictionary<ulong, WorldActorRuntimeState> despawnedActorsByEntityId = [];
    private readonly Dictionary<string, WorldActorActivityProfileDefinition> activityProfiles;
    private long populationRevision;

    public WorldActorStore(
        WorldActorRuntimeDocument document,
        SimulationEntityRegistry entityRegistry)
        : this(document, entityRegistry, Guid.NewGuid)
    {
    }

    internal WorldActorStore(
        WorldActorRuntimeDocument document,
        SimulationEntityRegistry entityRegistry,
        Func<Guid> runtimeIdFactory)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.entityRegistry = entityRegistry ?? throw new ArgumentNullException(nameof(entityRegistry));
        this.runtimeIdFactory = runtimeIdFactory ?? throw new ArgumentNullException(nameof(runtimeIdFactory));
        activityProfiles = document.ActivityProfiles.ToDictionary(
            profile => profile.Id,
            StringComparer.Ordinal);
    }

    public string ContentRevision => document.Revision;

    public int Count
    {
        get
        {
            lock (syncRoot)
            {
                return actorsByEntityId.Count;
            }
        }
    }

    public long PopulationRevision => Volatile.Read(ref populationRevision);

    public IReadOnlyList<WorldActorRuntimeState> ActivateAssignment(
        string worldId,
        string shardId,
        string workerRuntimeId)
    {
        if (!string.Equals(document.WorldId, worldId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(shardId)
            || string.IsNullOrWhiteSpace(workerRuntimeId))
        {
            throw new InvalidOperationException(
                "World actor assignment does not match the compiled runtime data.");
        }

        var definitions = document.Actors.ToDictionary(
            definition => definition.Id,
            StringComparer.Ordinal);
        lock (syncRoot)
        {
            actorsByEntityId.Clear();
            actorsByRuntimeId.Clear();
            despawnedActorsByEntityId.Clear();
            foreach (var spawn in document.SpawnInstances.OrderBy(
                         value => value.Id,
                         StringComparer.Ordinal))
            {
                var runtimeActorId = runtimeIdFactory();
                if (runtimeActorId == Guid.Empty || actorsByRuntimeId.ContainsKey(runtimeActorId))
                {
                    throw new InvalidOperationException(
                        "World actor runtime id allocation returned an invalid or reused id.");
                }

                var actor = new WorldActorRuntimeState(
                    entityRegistry.AllocateNetworkEntityId(),
                    runtimeActorId,
                    worldId,
                    shardId,
                    workerRuntimeId,
                    definitions[spawn.ActorId],
                    spawn);
                actorsByEntityId.Add(actor.NetworkEntityId, actor);
                actorsByRuntimeId.Add(actor.RuntimeActorId, actor);
            }

            Interlocked.Increment(ref populationRevision);

            return actorsByEntityId.Values
                .OrderBy(actor => actor.NetworkEntityId)
                .ToArray();
        }
    }

    public bool TryGetByEntityId(ulong entityId, out WorldActorRuntimeState? actor)
    {
        lock (syncRoot)
        {
            return actorsByEntityId.TryGetValue(entityId, out actor);
        }
    }

    public bool TryGetByRuntimeId(Guid runtimeActorId, out WorldActorRuntimeState? actor)
    {
        lock (syncRoot)
        {
            return actorsByRuntimeId.TryGetValue(runtimeActorId, out actor);
        }
    }

    public bool TryGetDespawnedByEntityId(
        ulong entityId,
        out WorldActorRuntimeState? actor)
    {
        lock (syncRoot)
        {
            return despawnedActorsByEntityId.TryGetValue(entityId, out actor);
        }
    }

    public bool TryDespawn(
        ulong entityId,
        out WorldActorRuntimeState? actor)
    {
        lock (syncRoot)
        {
            if (!actorsByEntityId.Remove(entityId, out actor))
            {
                return false;
            }

            actor.SetActive(false);
            actorsByRuntimeId.Remove(actor.RuntimeActorId);
            despawnedActorsByEntityId.Add(actor.NetworkEntityId, actor);
            Interlocked.Increment(ref populationRevision);
            return true;
        }
    }

    public bool TryGetActivityProfile(
        string activityProfileId,
        out WorldActorActivityProfileDefinition? profile)
    {
        return activityProfiles.TryGetValue(activityProfileId, out profile);
    }

    public void CopyActorsTo(List<WorldActorRuntimeState> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (syncRoot)
        {
            destination.Clear();
            destination.AddRange(actorsByEntityId.Values);
            destination.Sort(static (left, right) =>
                left.NetworkEntityId.CompareTo(right.NetworkEntityId));
        }
    }

    public IReadOnlyList<WorldActorRuntimeState> ListActors()
    {
        lock (syncRoot)
        {
            return actorsByEntityId.Values
                .OrderBy(actor => actor.NetworkEntityId)
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            actorsByEntityId.Clear();
            actorsByRuntimeId.Clear();
            despawnedActorsByEntityId.Clear();
            Interlocked.Increment(ref populationRevision);
        }
    }
}
