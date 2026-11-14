using Mmorpg.GameProtocol;

namespace Mmorpg.Tools.SimulationBotClient;

// What one bot knows about the world from the messages it already decodes: the world actors
// in its interest with the Mob positions of the snapshots, every combatant's health, its own
// combat state, the combat events it saw, the corpses near it, its open corpse view, world
// interaction and gathering state, its admin state, and its own items. The view holds no
// policy: it reports what the worker said, and a brain decides what to do with it.
// Not thread-safe; the session that owns it updates it on the poll thread.
public sealed class SimulationBotWorldView
{
    // Events nobody drained are dropped beyond this many, oldest first, and counted.
    public const int MaximumQueuedCombatEvents = 1024;

    private readonly Dictionary<ulong, SimulationBotWorldActor> actors = [];
    private readonly Dictionary<ulong, RealtimeCombatantState> combatants = [];
    private readonly Dictionary<Guid, RealtimeCorpsePresence> corpses = [];
    private readonly Dictionary<ulong, SimulationBotPlayer> players = [];
    private readonly Queue<RealtimeCombatEvent> combatEvents = new();
    private readonly List<RealtimeCorpsePresence> pendingCorpses = [];
    private uint pendingCorpseSequence;
    private int pendingCorpseChunks;
    private bool hasCorpseSnapshot;
    private uint corpseSnapshotSequence;

    public SimulationBotWorldView(string ownCharacterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownCharacterName);
        OwnCharacterName = ownCharacterName;
    }

    public string OwnCharacterName { get; }

    public ulong OwnEntityId { get; private set; }

    public RealtimeJoinAccepted? Session { get; private set; }

    public SimulationBotPosition OwnPosition { get; private set; }

    public RealtimeMovementPathProgress? OwnPathProgress { get; private set; }

    public uint LatestServerTick { get; private set; }

    public IReadOnlyDictionary<ulong, SimulationBotWorldActor> Actors => actors;

    public IReadOnlyDictionary<ulong, RealtimeCombatantState> Combatants => combatants;

    // The players in interest, by entity, with their newest known position.
    public IReadOnlyDictionary<ulong, SimulationBotPlayer> Players => players;

    public RealtimeOwnerCombatState? OwnCombat { get; private set; }

    // The corpses of the newest complete presence snapshot, by corpse.
    public IReadOnlyDictionary<Guid, RealtimeCorpsePresence> Corpses => corpses;

    public long CorpseSnapshots { get; private set; }

    // The corpse whose view is open, its newest committed revision, and whether its first
    // complete update arrived. Guid.Empty when no view is open.
    public Guid OpenCorpseId { get; private set; }

    public long OpenCorpseRevision { get; private set; } = -1;

    public bool OpenCorpseReady => OpenCorpseId != Guid.Empty && OpenCorpseRevision >= 0;

    public RealtimeCorpseViewClosed? LastCorpseViewClosed { get; private set; }

    public RealtimeWorldInteractionOpened? OpenInteraction { get; private set; }

    public RealtimeWorldInteractionClosed? LastInteractionClosed { get; private set; }

    public RealtimeGatheringState? Gathering { get; private set; }

    // Set once the worker told this character its admin state, which it sends only when it
    // allows the admin tools.
    public RealtimeAdminStateChanged? AdminState { get; private set; }

    public SimulationBotItemView Items { get; } = new();

    public long DroppedCombatEvents { get; private set; }

    public int QueuedCombatEvents => combatEvents.Count;

    public void ApplyJoin(RealtimeJoinAccepted session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        OwnEntityId = session.ControlledEntityId;
        OwnPosition = new SimulationBotPosition(
            session.InitialPlayerState.PositionX,
            session.InitialPlayerState.PositionY,
            session.InitialPlayerState.PositionZ);
        Items.ObserveCarry(session.CarryState);
    }

    // Only the newest owner snapshot reaches the view; the session drops reordered ones.
    public void ApplyOwnerSnapshot(RealtimeOwnerSimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LatestServerTick = snapshot.ServerTick;
        OwnPosition = new SimulationBotPosition(
            snapshot.State.PositionX,
            snapshot.State.PositionY,
            snapshot.State.PositionZ);
        OwnPathProgress = snapshot.PathProgress;
    }

    // A remote snapshot moves the players and the moving Mobs it carries.
    public void ApplySnapshot(RealtimeSimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var entity in snapshot.Entities)
        {
            if (actors.TryGetValue(entity.EntityId, out var actor))
            {
                actor.MoveTo(entity.State.PositionX, entity.State.PositionY, entity.State.PositionZ);
            }
            else if (players.TryGetValue(entity.EntityId, out var player))
            {
                player.MoveTo(entity.State.PositionX, entity.State.PositionY, entity.State.PositionZ);
            }
        }
    }

    public void ApplyEntitySpawn(RealtimeEntitySpawn spawn)
    {
        ArgumentNullException.ThrowIfNull(spawn);
        if (spawn.EntityId == OwnEntityId)
        {
            // A respawn or a teleport shows the own character again.
            OwnPosition = new SimulationBotPosition(
                spawn.InitialState.PositionX,
                spawn.InitialState.PositionY,
                spawn.InitialState.PositionZ);
            return;
        }

        players[spawn.EntityId] = new SimulationBotPlayer(
            spawn.EntityId,
            spawn.DisplayName,
            spawn.InitialState.PositionX,
            spawn.InitialState.PositionY,
            spawn.InitialState.PositionZ);
    }

    public void ApplyEntityDespawn(RealtimeEntityDespawn despawn)
    {
        ArgumentNullException.ThrowIfNull(despawn);
        players.Remove(despawn.EntityId);
        combatants.Remove(despawn.EntityId);
    }

    public void ApplyWorldActorSpawn(RealtimeWorldActorSpawn spawn)
    {
        ArgumentNullException.ThrowIfNull(spawn);
        actors[spawn.EntityId] = new SimulationBotWorldActor(spawn);
    }

    public void ApplyWorldActorState(RealtimeWorldActorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (actors.TryGetValue(state.EntityId, out var actor))
        {
            actor.Apply(state);
        }
    }

    public void ApplyWorldActorDespawn(RealtimeWorldActorDespawn despawn)
    {
        ArgumentNullException.ThrowIfNull(despawn);
        actors.Remove(despawn.EntityId);
        combatants.Remove(despawn.EntityId);
    }

    public void ApplyCombatantState(RealtimeCombatantState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!combatants.TryGetValue(state.EntityId, out var known)
            || state.StateRevision >= known.StateRevision)
        {
            combatants[state.EntityId] = state;
        }
    }

    public void ApplyOwnerCombatState(RealtimeOwnerCombatState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (OwnCombat is null || state.StateRevision >= OwnCombat.StateRevision)
        {
            OwnCombat = state;
        }
    }

    public void ApplyCombatEvents(RealtimeCombatEventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var combatEvent in batch.Events)
        {
            if (combatEvents.Count >= MaximumQueuedCombatEvents)
            {
                combatEvents.Dequeue();
                DroppedCombatEvents++;
            }

            combatEvents.Enqueue(combatEvent);
        }
    }

    // Hands the combat events that arrived since the last call to the caller, oldest first.
    public void DrainCombatEvents(ICollection<RealtimeCombatEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        while (combatEvents.Count > 0)
        {
            destination.Add(combatEvents.Dequeue());
        }
    }

    // A presence snapshot arrives in chunks; the corpses change once every chunk of the
    // newest snapshot arrived, and an older or incomplete snapshot never replaces them.
    public void ApplyCorpsePresence(RealtimeCorpsePresenceSnapshotChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (hasCorpseSnapshot && chunk.SnapshotSequence <= corpseSnapshotSequence)
        {
            return;
        }

        if (pendingCorpseChunks == 0 || chunk.SnapshotSequence != pendingCorpseSequence)
        {
            if (pendingCorpseChunks > 0 && chunk.SnapshotSequence < pendingCorpseSequence)
            {
                return;
            }

            pendingCorpses.Clear();
            pendingCorpseSequence = chunk.SnapshotSequence;
            pendingCorpseChunks = 0;
        }

        pendingCorpses.AddRange(chunk.Corpses);
        pendingCorpseChunks++;
        if (pendingCorpseChunks < Math.Max(1, (int)chunk.ChunkCount))
        {
            return;
        }

        corpses.Clear();
        foreach (var corpse in pendingCorpses)
        {
            corpses[corpse.CorpseId] = corpse;
        }

        pendingCorpses.Clear();
        pendingCorpseChunks = 0;
        hasCorpseSnapshot = true;
        corpseSnapshotSequence = chunk.SnapshotSequence;
        CorpseSnapshots++;
    }

    // A successful Open makes its corpse the open one; the view chunks that follow carry the
    // revision that loot and close operations must expect.
    public void ApplyCorpseInteractionResult(RealtimeCorpseInteractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.OperationKind == RealtimeCorpseInteractionKind.Open && result.Succeeded)
        {
            if (OpenCorpseId != result.CorpseId)
            {
                OpenCorpseId = result.CorpseId;
                OpenCorpseRevision = -1;
            }
        }
        else if (result.OperationKind == RealtimeCorpseInteractionKind.Close
            && result.Succeeded
            && OpenCorpseId == result.CorpseId)
        {
            ClearOpenCorpse();
        }
    }

    public void ApplyCorpseViewState(RealtimeCorpseViewStateChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.CorpseId != OpenCorpseId)
        {
            if (OpenCorpseId != Guid.Empty || chunk.UpdateKind != RealtimeCorpseViewUpdateKind.Snapshot)
            {
                return;
            }

            // The first view snapshot may come before the Open result, so a snapshot while
            // no view is open starts the view.
            OpenCorpseId = chunk.CorpseId;
            OpenCorpseRevision = -1;
        }

        if (chunk.ChunkIndex + 1 >= chunk.ChunkCount && chunk.Revision >= OpenCorpseRevision)
        {
            OpenCorpseRevision = chunk.Revision;
        }
    }

    public void ApplyCorpseViewClosed(RealtimeCorpseViewClosed closed)
    {
        ArgumentNullException.ThrowIfNull(closed);
        LastCorpseViewClosed = closed;
        if (closed.CorpseId == OpenCorpseId)
        {
            ClearOpenCorpse();
        }
    }

    public void ApplyWorldInteractionOpened(RealtimeWorldInteractionOpened opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        OpenInteraction = opened;
    }

    public void ApplyWorldInteractionResult(RealtimeWorldInteractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.OperationKind == RealtimeWorldInteractionOperationKind.Close
            && result.Succeeded
            && OpenInteraction?.InteractionSessionId == result.InteractionSessionId)
        {
            OpenInteraction = null;
            Gathering = null;
        }
    }

    public void ApplyWorldInteractionClosed(RealtimeWorldInteractionClosed closed)
    {
        ArgumentNullException.ThrowIfNull(closed);
        LastInteractionClosed = closed;
        if (OpenInteraction?.InteractionSessionId == closed.InteractionSessionId)
        {
            OpenInteraction = null;
            Gathering = null;
        }
    }

    public void ApplyGatheringState(RealtimeGatheringState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Gathering = state;
    }

    public void ApplyAdminState(RealtimeAdminStateChanged state)
    {
        ArgumentNullException.ThrowIfNull(state);
        AdminState = state;
    }

    private void ClearOpenCorpse()
    {
        OpenCorpseId = Guid.Empty;
        OpenCorpseRevision = -1;
    }
}

// A world actor in interest: its identity and kind from its spawn, and its newest position.
public sealed class SimulationBotWorldActor
{
    public SimulationBotWorldActor(RealtimeWorldActorSpawn spawn)
    {
        ArgumentNullException.ThrowIfNull(spawn);
        EntityId = spawn.EntityId;
        RuntimeActorId = spawn.RuntimeActorId;
        ActorDefinitionId = spawn.ActorDefinitionId;
        SpawnDefinitionId = spawn.SpawnDefinitionId;
        Kind = spawn.Kind;
        Disposition = spawn.Disposition;
        Aggression = spawn.Aggression;
        Level = spawn.Level;
        MenuOptions = spawn.MenuOptions;
        StateRevision = spawn.StateRevision;
        InteractionRevision = spawn.InteractionRevision;
        IsActive = spawn.IsActive;
        Position = new SimulationBotPosition(spawn.PositionX, spawn.PositionY, spawn.PositionZ);
    }

    public ulong EntityId { get; }

    public Guid RuntimeActorId { get; }

    public string ActorDefinitionId { get; }

    public string SpawnDefinitionId { get; }

    public RealtimeWorldActorKind Kind { get; }

    public RealtimeWorldActorDisposition Disposition { get; }

    public RealtimeWorldActorAggression Aggression { get; }

    public ushort Level { get; }

    public IReadOnlyList<RealtimeWorldActorMenuOption> MenuOptions { get; }

    public long StateRevision { get; private set; }

    public long InteractionRevision { get; private set; }

    public bool IsActive { get; private set; }

    public SimulationBotPosition Position { get; private set; }

    internal void Apply(RealtimeWorldActorState state)
    {
        if (state.StateRevision < StateRevision)
        {
            return;
        }

        StateRevision = state.StateRevision;
        InteractionRevision = state.InteractionRevision;
        IsActive = state.IsActive;
        Position = new SimulationBotPosition(state.PositionX, state.PositionY, state.PositionZ);
    }

    internal void MoveTo(float x, float y, float z)
    {
        Position = new SimulationBotPosition(x, y, z);
    }
}

// A remote player in interest and its newest known position.
public sealed class SimulationBotPlayer(ulong entityId, string displayName, float x, float y, float z)
{
    public ulong EntityId { get; } = entityId;

    public string DisplayName { get; } = displayName;

    public SimulationBotPosition Position { get; private set; } = new(x, y, z);

    internal void MoveTo(float positionX, float positionY, float positionZ)
    {
        Position = new SimulationBotPosition(positionX, positionY, positionZ);
    }
}
