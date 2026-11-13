using System;
using System.Collections.Generic;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The world actors of the joined session: their reliable presence and their state.
    public interface IRealtimeWorldActorChannel
    {
        event Action<RealtimeWorldActorSpawn> WorldActorSpawned;

        // A state of a spawned world actor that is not older than its newest one.
        event Action<RealtimeWorldActorState> WorldActorStateReceived;

        // Raised for a despawn, and for every spawned world actor when the session ends, after
        // the players.
        event Action<RealtimeWorldActorDespawn> WorldActorDespawned;

        // A copy of the spawned world actors, for a reader that comes after their spawn events.
        IReadOnlyCollection<RealtimeWorldActorSpawn> SpawnedWorldActors { get; }
    }

    internal sealed class RealtimeWorldActorChannel : IRealtimeWorldActorChannel
    {
        private readonly RealtimeSession session;
        private readonly RealtimePresenceRegistry presence;

        public RealtimeWorldActorChannel(
            RealtimeSession session,
            RealtimePresenceRegistry presence,
            RealtimeMessageTable messages)
        {
            this.session = session;
            this.presence = presence;
            presence.WorldActorCleared += RaiseWorldActorDespawned;
            messages.Register(
                RealtimeMessageType.WorldActorSpawn,
                HandleWorldActorSpawn,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_world_actor_spawn",
                "World actor presence arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.WorldActorState,
                HandleWorldActorState,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_world_actor_state",
                "World actor state arrived without an active simulation session.");
            messages.Register(
                RealtimeMessageType.WorldActorDespawn,
                HandleWorldActorDespawn,
                RealtimeStates.Joined,
                RealtimeStates.Leaving,
                "unexpected_world_actor_despawn",
                "World actor despawn arrived outside an active simulation session.");
        }

        public event Action<RealtimeWorldActorSpawn> WorldActorSpawned;

        public event Action<RealtimeWorldActorState> WorldActorStateReceived;

        public event Action<RealtimeWorldActorDespawn> WorldActorDespawned;

        public IReadOnlyCollection<RealtimeWorldActorSpawn> SpawnedWorldActors
        {
            get { return presence.CopyWorldActors(); }
        }

        // A spawn of the same actor replaces it unless it is older than its newest state.
        private void HandleWorldActorSpawn(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeWorldActorSpawn(
                    packet,
                    out var spawn,
                    out var error))
            {
                session.FailProtocol("invalid_world_actor_spawn", error);
                return;
            }

            if (presence.TryGetWorldActor(spawn.EntityId, out var existing)
                && !HasSameWorldActorPresenceIdentity(existing, spawn))
            {
                session.FailProtocol(
                    "world_actor_entity_conflict",
                    "SimulationWorker reused an active world actor entity id.");
                return;
            }

            if (presence.IsPlayer(spawn.EntityId))
            {
                session.FailProtocol(
                    "world_actor_entity_scope_conflict",
                    "SimulationWorker reused a player network entity id for a world actor.");
                return;
            }

            if (presence.IsOlderThanWorldActorState(spawn.EntityId, spawn.StateRevision))
            {
                return;
            }

            presence.AddWorldActor(spawn);
            WorldActorSpawned?.Invoke(spawn);
        }

        private void HandleWorldActorState(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeWorldActorState(
                    packet,
                    out var state,
                    out var error))
            {
                session.FailProtocol("invalid_world_actor_state", error);
                return;
            }

            if (!presence.IsWorldActor(state.EntityId))
            {
                session.FailProtocol(
                    "world_actor_state_without_spawn",
                    "World actor state arrived before reliable actor presence.");
                return;
            }

            if (presence.IsOlderThanWorldActorState(state.EntityId, state.StateRevision))
            {
                return;
            }

            presence.SetWorldActorRevision(state.EntityId, state.StateRevision);
            WorldActorStateReceived?.Invoke(state);
        }

        // A despawn of an actor the client does not know is raised too.
        private void HandleWorldActorDespawn(byte[] packet)
        {
            if (!RealtimeProtocol.TryDecodeWorldActorDespawn(
                    packet,
                    out var despawn,
                    out var error))
            {
                session.FailProtocol("invalid_world_actor_despawn", error);
                return;
            }

            if (presence.TryGetWorldActor(despawn.EntityId, out var existing)
                && existing.RuntimeActorId != despawn.RuntimeActorId)
            {
                session.FailProtocol(
                    "world_actor_despawn_conflict",
                    "World actor despawn runtime identity does not match presence.");
                return;
            }

            presence.RemoveWorldActor(despawn.EntityId);
            WorldActorDespawned?.Invoke(despawn);
        }

        private void RaiseWorldActorDespawned(RealtimeWorldActorDespawn despawn)
        {
            WorldActorDespawned?.Invoke(despawn);
        }

        private static bool HasSameWorldActorPresenceIdentity(
            RealtimeWorldActorSpawn first,
            RealtimeWorldActorSpawn second)
        {
            return first.RuntimeActorId == second.RuntimeActorId
                && string.Equals(
                    first.ActorDefinitionId,
                    second.ActorDefinitionId,
                    StringComparison.Ordinal)
                && string.Equals(
                    first.SpawnDefinitionId,
                    second.SpawnDefinitionId,
                    StringComparison.Ordinal)
                && first.Kind == second.Kind
                && string.Equals(first.FactionId, second.FactionId, StringComparison.Ordinal)
                && first.Disposition == second.Disposition
                && first.Level == second.Level
                && first.Aggression == second.Aggression
                && string.Equals(
                    first.PresentationArchetypeId,
                    second.PresentationArchetypeId,
                    StringComparison.Ordinal)
                && first.BoundsCenterX.Equals(second.BoundsCenterX)
                && first.BoundsCenterY.Equals(second.BoundsCenterY)
                && first.BoundsCenterZ.Equals(second.BoundsCenterZ)
                && first.BoundsSizeX.Equals(second.BoundsSizeX)
                && first.BoundsSizeY.Equals(second.BoundsSizeY)
                && first.BoundsSizeZ.Equals(second.BoundsSizeZ);
        }
    }
}
