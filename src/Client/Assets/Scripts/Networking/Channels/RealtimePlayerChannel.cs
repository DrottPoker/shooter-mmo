using System;
using System.Collections.Generic;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The other players of the joined session, from their reliable spawn and despawn.
    // Snapshots never create or remove them.
    public interface IRealtimePlayerChannel
    {
        event Action<RealtimeEntitySpawn> EntitySpawned;

        // Raised for a despawn, and for every spawned player when the session ends.
        event Action<RealtimeEntityDespawn> EntityDespawned;

        // A copy of the spawned players, for a reader that comes after their spawn events.
        IReadOnlyCollection<RealtimeEntitySpawn> SpawnedEntities { get; }

        int SpawnedEntityCount { get; }
    }

    internal sealed class RealtimePlayerChannel : IRealtimePlayerChannel
    {
        private readonly RealtimeSession session;
        private readonly RealtimePresenceRegistry presence;

        public RealtimePlayerChannel(
            RealtimeSession session,
            RealtimePresenceRegistry presence,
            RealtimeMessageTable messages)
        {
            this.session = session;
            this.presence = presence;
            presence.PlayerCleared += RaiseEntityDespawned;
            messages.Register(
                RealtimeMessageType.EntitySpawn,
                HandleEntitySpawn,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_entity_spawn",
                "Entity spawn arrived before the simulation session was joined.");
            messages.Register(
                RealtimeMessageType.EntityDespawn,
                HandleEntityDespawn,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_entity_despawn",
                "Entity despawn arrived outside an active simulation session.");
        }

        public event Action<RealtimeEntitySpawn> EntitySpawned;

        public event Action<RealtimeEntityDespawn> EntityDespawned;

        public IReadOnlyCollection<RealtimeEntitySpawn> SpawnedEntities
        {
            get { return presence.CopyPlayers(); }
        }

        public int SpawnedEntityCount
        {
            get { return presence.PlayerCount; }
        }

        private void HandleEntitySpawn(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeEntitySpawn(packet, out var spawn, out var error))
            {
                session.FailProtocol("invalid_entity_spawn", error);
                return;
            }

            if (presence.IsWorldActor(spawn.EntityId))
            {
                session.FailProtocol(
                    "entity_scope_conflict",
                    "SimulationWorker reused a world actor network entity id for a player.");
                return;
            }

            if (presence.TryGetPlayer(spawn.EntityId, out var existing)
                && (!string.Equals(existing.PersistentId, spawn.PersistentId, StringComparison.OrdinalIgnoreCase)
                    || existing.Kind != spawn.Kind
                    || !string.Equals(existing.ArchetypeId, spawn.ArchetypeId, StringComparison.Ordinal)))
            {
                session.FailProtocol(
                    "entity_id_conflict",
                    "SimulationWorker reused an active network entity id for another entity.");
                return;
            }

            presence.AddPlayer(spawn);
            EntitySpawned?.Invoke(spawn);
        }

        // The controlled entity keeps its look through a despawn of its id.
        private void HandleEntityDespawn(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeEntityDespawn(packet, out var despawn, out var error))
            {
                session.FailProtocol("invalid_entity_despawn", error);
                return;
            }

            presence.RemovePlayer(
                despawn.EntityId,
                despawn.EntityId == session.MovementSession.ControlledEntityId);
            EntityDespawned?.Invoke(despawn);
        }

        private void RaiseEntityDespawned(RealtimeEntityDespawn despawn)
        {
            EntityDespawned?.Invoke(despawn);
        }
    }
}
