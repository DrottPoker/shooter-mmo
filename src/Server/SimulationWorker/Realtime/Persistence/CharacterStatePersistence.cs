using Microsoft.Extensions.Logging;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Realtime;

// Keeps the place and vitals of every player a person plays; a synthetic bot has no stored state.
// A new entity starts where the character was saved when the save names this World and the
// collision revision the worker runs, and otherwise at the World spawn; it keeps the saved health
// and mana in any World. The players whose state changed are saved at the checkpoint interval,
// lingering bodies too, since they can still lose health, at most one call at a time, and a failed
// call's players go again with the next one. When the entity of a session the worker still holds
// is removed, such as a lingering body whose time is up or whose death committed, and at shutdown,
// the final state is captured for the release that follows, whichever path runs it. A dead
// player is saved as its respawn leaves it: at the World spawn with full health and mana. The
// vitals are read from combat whenever they are needed: the removed hooks run in reverse
// registration order, so this feature, added after combat, sees a player go while combat still
// holds it. Loop-confined; the checkpoint call runs in CharacterStateAuthorityOperations.
internal sealed class CharacterStatePersistence(
    SimulationWorkerConfig config,
    string collisionRevision,
    ICollisionWorld collisionWorld,
    WorldCollisionStreamingStore? collisionStreamingStore,
    ActiveSimulationSessionStore sessionStore,
    SimulationEntityRegistry entityRegistry,
    CombatSystem combat,
    CharacterFinalStateStore finalStates,
    RealtimeOperationTracker operationTracker,
    CharacterStateAuthorityOperations? operations,
    ILogger logger) : IRealtimeCompletionHandler
{
    private const float MaximumCoordinate = 1_000_000f;
    private const float MaximumYawDegrees = 360f;

    private readonly uint checkpointIntervalTicks = Math.Max(
        1u,
        CombatTicks.FromSeconds(
            (float)config.CharacterCheckpointInterval.TotalSeconds,
            config.MovementSimulation.TickRateHz));

    private readonly Dictionary<ulong, TrackedPlayer> players = [];

    // What a placement decided for a new entity of the session, until its join hook takes it.
    private readonly Dictionary<Guid, RestoredState?> placements = [];

    private long lastSequence;
    private uint nextCheckpointTick;
    private bool checkpointScheduled;
    private bool checkpointInFlight;

    public void AddTickPhase(SimulationTickPhases phases)
    {
        phases.Add(SimulationTickOrder.CharacterCheckpoint, "character checkpoint", (in SimulationTick tick) => Checkpoint(in tick));
    }

    // Where a joining session's new entity starts. A reconnect keeps its entity and its place.
    public PlayerMovementState? Place(ActiveSimulationSession session)
    {
        if (session.IsSyntheticBot
            || (entityRegistry.TryGetPlayerByCharacterId(session.CharacterId, out var existing)
                && existing!.Session.SimulationSessionId == session.SimulationSessionId))
        {
            return null;
        }

        var restored = Resolve(session);
        placements[session.SimulationSessionId] = restored;
        if (restored is not { RestoresPosition: true } placed)
        {
            return null;
        }

        var state = placed.State;
        // As a teleport does, the collision around the saved place loads before the player
        // stands on it.
        collisionStreamingStore?.EnsureLoaded(new SimulationVector3(state.PositionX, state.PositionY, state.PositionZ));
        return PlayerMovementSimulation.CreateInitialState(
            config.MovementSimulation,
            collisionWorld,
            state.PositionX,
            state.PositionY,
            state.PositionZ,
            state.YawDegrees);
    }

    // Combat made the player a combatant with full vitals; a new entity takes the saved ones.
    public void OnPlayerJoined(RealtimePeerContext context, PlayerSimulationEntity entity)
    {
        var session = entity.Session;
        if (session.IsSyntheticBot)
        {
            return;
        }

        var isNewEntity = placements.Remove(session.SimulationSessionId, out var restored);
        if (players.ContainsKey(entity.NetworkEntityId)
            || !combat.TryGetPlayerCombatant(entity.NetworkEntityId, out var combatant))
        {
            return;
        }

        if (isNewEntity && restored is { } state)
        {
            combat.TryRestoreVitals(combatant, state.State.Health, state.State.Mana);
        }

        players.Add(entity.NetworkEntityId, new TrackedPlayer(entity)
        {
            LastSaved = isNewEntity ? restored?.Saved : null
        });
    }

    // A session whose entity went while the worker holds it, such as a lingering body's, is
    // released after this; an invalidated or replaced session is released by nobody here, so a
    // state captured for it is dropped. Combat still holds the player, because its removed hook
    // runs after this one.
    public void OnPlayerRemoved(RealtimePeerContext context, ActiveSimulationSession session, ulong entityId)
    {
        if (!players.Remove(entityId, out var player))
        {
            return;
        }

        if (IsHeld(session))
        {
            Capture(player, session);
        }
        else
        {
            finalStates.Remove(session.CharacterId, session.SimulationSessionId);
        }
    }

    public void Complete(RealtimeOperationResult completion)
    {
        if (completion is not CharacterCheckpointCompleted completed)
        {
            return;
        }

        checkpointInFlight = false;
        for (var index = 0; index < completed.AnsweredCount; index++)
        {
            var entry = completed.Entries[index];
            if (players.TryGetValue(entry.EntityId, out var player)
                && player.Entity.Session.SimulationSessionId == entry.Session.SimulationSessionId)
            {
                player.LastSaved = entry.State;
            }
        }
    }

    // Runs once the loop stopped: the state of every player whose session the worker still
    // holds goes into one last checkpoint and stays for the session's release in case that
    // checkpoint fails. A release after the worker went offline saves nothing, because going
    // offline ends the sessions.
    public async Task FlushOnShutdownAsync()
    {
        var entries = new List<CharacterCheckpointEntry>();
        foreach (var (entityId, player) in players.OrderBy(pair => pair.Key))
        {
            var session = player.Entity.Session;
            if (!IsHeld(session) || Snapshot(player) is not { } snapshot)
            {
                continue;
            }

            var sequence = ++lastSequence;
            finalStates.Capture(session.CharacterId, session.SimulationSessionId, new CharacterFinalState(sequence, snapshot));
            entries.Add(new CharacterCheckpointEntry(entityId, session, sequence, snapshot));
        }

        players.Clear();
        placements.Clear();
        if (operations is null || entries.Count == 0)
        {
            return;
        }

        var answered = await operations.SendAsync(entries);
        for (var index = 0; index < answered; index++)
        {
            var entry = entries[index];
            finalStates.Remove(entry.Session.CharacterId, entry.Session.SimulationSessionId, entry.Sequence);
        }

        logger.LogInformation(
            "[SIMULATION] Saved the place and vitals of {Answered} of {Count} players at shutdown.",
            answered,
            entries.Count);
    }

    // Runs every tick and does nothing until the interval is due, so it allocates nothing per
    // player per tick. A due checkpoint waits while the previous call is in flight.
    private void Checkpoint(in SimulationTick tick)
    {
        if (operations is null)
        {
            return;
        }

        if (!checkpointScheduled)
        {
            nextCheckpointTick = tick.ServerTick + checkpointIntervalTicks;
            checkpointScheduled = true;
            return;
        }

        if (checkpointInFlight || !CombatTicks.IsReached(tick.ServerTick, nextCheckpointTick))
        {
            return;
        }

        nextCheckpointTick = tick.ServerTick + checkpointIntervalTicks;
        List<CharacterCheckpointEntry>? entries = null;
        foreach (var entity in tick.Players)
        {
            if (!players.TryGetValue(entity.NetworkEntityId, out var player)
                || Snapshot(player) is not { } snapshot
                || player.LastSaved == snapshot)
            {
                continue;
            }

            (entries ??= []).Add(new CharacterCheckpointEntry(entity.NetworkEntityId, entity.Session, ++lastSequence, snapshot));
        }

        if (entries is null)
        {
            return;
        }

        checkpointInFlight = true;
        operationTracker.Track(operations.SendCheckpointAsync(this, entries));
    }

    private void Capture(TrackedPlayer player, ActiveSimulationSession session)
    {
        if (Snapshot(player) is { } snapshot)
        {
            finalStates.Capture(
                session.CharacterId,
                session.SimulationSessionId,
                new CharacterFinalState(++lastSequence, snapshot));
        }
    }

    // The state as the player has it now, or as the respawn leaves a dead one; null when combat
    // does not hold the player, or for a state outside what GameService stores, which only a
    // fault in the simulation could make.
    private CharacterStateSnapshot? Snapshot(TrackedPlayer player)
    {
        if (!combat.TryGetPlayerCombatant(player.Entity.NetworkEntityId, out var combatant))
        {
            return null;
        }

        CharacterStateSnapshot snapshot;
        if (combatant.IsAlive)
        {
            var state = player.Entity.Movement.State;
            snapshot = new CharacterStateSnapshot(
                state.PositionX,
                state.PositionY,
                state.PositionZ,
                state.YawDegrees,
                Math.Max(1, combatant.Health),
                combatant.Mana);
        }
        else
        {
            var spawn = config.MovementSpawn;
            snapshot = new CharacterStateSnapshot(
                spawn.X,
                spawn.Y,
                spawn.Z,
                spawn.YawDegrees,
                combatant.MaximumHealth,
                combatant.MaximumMana);
        }

        if (IsCoordinate(snapshot.PositionX)
            && IsCoordinate(snapshot.PositionY)
            && IsCoordinate(snapshot.PositionZ)
            && float.IsFinite(snapshot.YawDegrees)
            && Math.Abs(snapshot.YawDegrees) <= MaximumYawDegrees)
        {
            return snapshot;
        }

        logger.LogWarning(
            "[SIMULATION] Did not save the state of character {CharacterId}, whose position or facing is not finite.",
            player.Entity.Session.CharacterId);
        return null;
    }

    // A state this worker captured for the session is newer than the one GameService returned.
    // A saved place counts only in this World with the collision this worker runs; the saved
    // health and mana count everywhere.
    private RestoredState? Resolve(ActiveSimulationSession session)
    {
        if (finalStates.TryGet(session.CharacterId, session.SimulationSessionId, out var captured))
        {
            return new RestoredState(captured.State, RestoresPosition: true, Saved: null);
        }

        if (session.SavedState is not { } saved)
        {
            return null;
        }

        var state = new CharacterStateSnapshot(
            saved.PositionX,
            saved.PositionY,
            saved.PositionZ,
            saved.YawDegrees,
            saved.Health,
            saved.Mana);
        var restoresPosition = string.Equals(saved.WorldId, config.WorldId, StringComparison.Ordinal)
            && string.Equals(saved.CollisionRevision, collisionRevision, StringComparison.Ordinal);
        return new RestoredState(state, restoresPosition, restoresPosition ? state : null);
    }

    private bool IsHeld(ActiveSimulationSession session)
    {
        return sessionStore.TryGet(session.CharacterId, out var current)
            && current!.SimulationSessionId == session.SimulationSessionId;
    }

    private static bool IsCoordinate(float value)
    {
        return float.IsFinite(value) && Math.Abs(value) <= MaximumCoordinate;
    }

    // State restores the vitals and, when RestoresPosition, the place. Saved is what GameService
    // holds as far as the worker knows, so an unchanged player is not saved again.
    private readonly record struct RestoredState(
        CharacterStateSnapshot State,
        bool RestoresPosition,
        CharacterStateSnapshot? Saved);

    private sealed class TrackedPlayer(PlayerSimulationEntity entity)
    {
        public PlayerSimulationEntity Entity { get; } = entity;

        // What GameService holds for the character as far as the worker knows, or null.
        public CharacterStateSnapshot? LastSaved { get; set; }
    }
}
