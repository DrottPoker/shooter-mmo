using System;
using System.Collections.Generic;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The presence of the joined session: the spawned players with the latest look of each and
    // of the controlled player, and the spawned world actors with their newest state revision.
    // The player, look, world actor, and combat channels check and change it; the session clears
    // it; a reader that comes after the spawn events gets a copy.
    internal sealed class RealtimePresenceRegistry
    {
        private readonly Dictionary<ulong, RealtimeEntitySpawn> players =
            new Dictionary<ulong, RealtimeEntitySpawn>();
        private readonly Dictionary<ulong, RealtimeCharacterAppearance> looks =
            new Dictionary<ulong, RealtimeCharacterAppearance>();
        private readonly Dictionary<ulong, RealtimeWorldActorSpawn> worldActors =
            new Dictionary<ulong, RealtimeWorldActorSpawn>();
        private readonly Dictionary<ulong, long> worldActorRevisions =
            new Dictionary<ulong, long>();

        // Raised by Clear for every player, then every world actor, that it removes; the player
        // and world actor channels raise their despawn events from them.
        public event Action<RealtimeEntityDespawn> PlayerCleared;

        public event Action<RealtimeWorldActorDespawn> WorldActorCleared;

        public int PlayerCount
        {
            get { return players.Count; }
        }

        public bool IsPlayer(ulong entityId)
        {
            return players.ContainsKey(entityId);
        }

        public bool TryGetPlayer(ulong entityId, out RealtimeEntitySpawn spawn)
        {
            return players.TryGetValue(entityId, out spawn);
        }

        public bool IsWorldActor(ulong entityId)
        {
            return worldActors.ContainsKey(entityId);
        }

        public bool TryGetWorldActor(ulong entityId, out RealtimeWorldActorSpawn spawn)
        {
            return worldActors.TryGetValue(entityId, out spawn);
        }

        // Whether a spawn or state of the world actor is older than its newest state revision.
        public bool IsOlderThanWorldActorState(ulong entityId, long stateRevision)
        {
            return worldActorRevisions.TryGetValue(entityId, out var newest) && stateRevision < newest;
        }

        // A combatant has presence as a spawned player, a spawned world actor, or the controlled
        // entity, which the worker never spawns for its own client.
        public bool HasCombatantPresence(ulong entityId, ulong controlledEntityId)
        {
            return players.ContainsKey(entityId)
                || worldActors.ContainsKey(entityId)
                || entityId == controlledEntityId;
        }

        public bool TryGetLook(ulong entityId, out RealtimeCharacterAppearance look)
        {
            return looks.TryGetValue(entityId, out look);
        }

        // Adds or replaces a player with the look it spawned with.
        public void AddPlayer(RealtimeEntitySpawn spawn)
        {
            players[spawn.EntityId] = spawn;
            looks[spawn.EntityId] = spawn.Appearance;
        }

        public void RemovePlayer(ulong entityId, bool keepLook)
        {
            players.Remove(entityId);
            if (!keepLook)
            {
                looks.Remove(entityId);
            }
        }

        public void SetLook(ulong entityId, RealtimeCharacterAppearance look)
        {
            looks[entityId] = look;
        }

        // Adds or replaces a world actor with the state revision it spawned with.
        public void AddWorldActor(RealtimeWorldActorSpawn spawn)
        {
            worldActors[spawn.EntityId] = spawn;
            worldActorRevisions[spawn.EntityId] = spawn.StateRevision;
        }

        public void SetWorldActorRevision(ulong entityId, long stateRevision)
        {
            worldActorRevisions[entityId] = stateRevision;
        }

        public void RemoveWorldActor(ulong entityId)
        {
            worldActors.Remove(entityId);
            worldActorRevisions.Remove(entityId);
        }

        public List<RealtimeEntitySpawn> CopyPlayers()
        {
            return new List<RealtimeEntitySpawn>(players.Values);
        }

        public List<RealtimeWorldActorSpawn> CopyWorldActors()
        {
            return new List<RealtimeWorldActorSpawn>(worldActors.Values);
        }

        // Removes every player with its look and announces each, then removes every world actor
        // and announces each, with the reason the session ended.
        public void Clear(string reason)
        {
            var entityIds = new List<ulong>(players.Keys);
            players.Clear();
            looks.Clear();
            for (var index = 0; index < entityIds.Count; index++)
            {
                PlayerCleared?.Invoke(new RealtimeEntityDespawn(entityIds[index], reason));
            }

            var actors = new List<RealtimeWorldActorSpawn>(worldActors.Values);
            worldActors.Clear();
            worldActorRevisions.Clear();
            for (var index = 0; index < actors.Count; index++)
            {
                var actor = actors[index];
                WorldActorCleared?.Invoke(new RealtimeWorldActorDespawn(
                    actor.EntityId,
                    actor.RuntimeActorId,
                    reason));
            }
        }
    }
}
