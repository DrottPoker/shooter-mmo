using System;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Tests.PlayMode
{
    // Counts what the hub raises during the live stack test: the session events, the owner
    // snapshots, the world actor spawns, and the owner's combat state. Reset starts a new count,
    // as before each join.
    internal sealed class LiveStackProbe : IDisposable
    {
        private readonly RealtimeSimulationClient hub;

        public LiveStackProbe(RealtimeSimulationClient hub)
        {
            this.hub = hub;
            hub.Session.SessionJoined += OnSessionJoined;
            hub.Session.SessionEnded += OnSessionEnded;
            hub.Session.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;
            hub.Movement.OwnerSimulationSnapshotReceived += OnOwnerSnapshot;
            hub.WorldActors.WorldActorSpawned += OnWorldActorSpawned;
            hub.Combat.OwnerCombatStateReceived += OnOwnerCombatState;
        }

        public int SessionJoinedCount { get; private set; }

        public int SessionEndedCount { get; private set; }

        public int UnexpectedlyDisconnectedCount { get; private set; }

        public int OwnerSnapshotCount { get; private set; }

        public RealtimeOwnerSimulationSnapshot LatestOwnerSnapshot { get; private set; }

        public int WorldActorSpawnCount { get; private set; }

        public RealtimeOwnerCombatState LatestOwnerCombatState { get; private set; }

        public void Reset()
        {
            SessionJoinedCount = 0;
            SessionEndedCount = 0;
            UnexpectedlyDisconnectedCount = 0;
            OwnerSnapshotCount = 0;
            LatestOwnerSnapshot = null;
            WorldActorSpawnCount = 0;
            LatestOwnerCombatState = null;
        }

        public void Dispose()
        {
            hub.Session.SessionJoined -= OnSessionJoined;
            hub.Session.SessionEnded -= OnSessionEnded;
            hub.Session.UnexpectedlyDisconnected -= OnUnexpectedlyDisconnected;
            hub.Movement.OwnerSimulationSnapshotReceived -= OnOwnerSnapshot;
            hub.WorldActors.WorldActorSpawned -= OnWorldActorSpawned;
            hub.Combat.OwnerCombatStateReceived -= OnOwnerCombatState;
        }

        private void OnSessionJoined()
        {
            SessionJoinedCount++;
        }

        private void OnSessionEnded(RealtimeClientError error)
        {
            SessionEndedCount++;
        }

        private void OnUnexpectedlyDisconnected(RealtimeClientError error)
        {
            UnexpectedlyDisconnectedCount++;
        }

        private void OnOwnerSnapshot(RealtimeOwnerSimulationSnapshot snapshot)
        {
            OwnerSnapshotCount++;
            LatestOwnerSnapshot = snapshot;
        }

        private void OnWorldActorSpawned(RealtimeWorldActorSpawn spawn)
        {
            WorldActorSpawnCount++;
        }

        private void OnOwnerCombatState(RealtimeOwnerCombatState state)
        {
            LatestOwnerCombatState = state;
        }
    }
}
