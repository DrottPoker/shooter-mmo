using System;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    [DisallowMultipleComponent]
    public sealed class WorldActorClientController : MonoBehaviour
    {
        private RealtimeSimulationClient simulationClient;
        private bool initialized;

        public WorldActorClientState State { get; } = new WorldActorClientState();

        public void Initialize(RealtimeSimulationClient realtimeClient)
        {
            if (realtimeClient == null)
            {
                throw new ArgumentNullException(nameof(realtimeClient));
            }

            if (initialized)
            {
                Unsubscribe();
            }

            simulationClient = realtimeClient;
            simulationClient.WorldActorSpawned += OnSpawned;
            simulationClient.WorldActorStateReceived += OnStateReceived;
            simulationClient.WorldActorDespawned += OnDespawned;
            simulationClient.SessionEnded += OnSessionEnded;
            initialized = true;

            State.Clear();
            foreach (var spawn in simulationClient.SpawnedWorldActors)
            {
                OnSpawned(spawn);
            }
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        private void OnSpawned(RealtimeWorldActorSpawn spawn)
        {
            if (!State.TryApplySpawn(spawn, out var error))
            {
                simulationClient.DisconnectForClientFailure(
                    "world_actor_state_conflict",
                    error);
            }
        }

        private void OnStateReceived(RealtimeWorldActorState state)
        {
            if (!State.TryApplyState(state, out var error))
            {
                simulationClient.DisconnectForClientFailure(
                    "world_actor_state_conflict",
                    error);
            }
        }

        private void OnDespawned(RealtimeWorldActorDespawn despawn)
        {
            if (!State.TryRemove(despawn, out var error))
            {
                simulationClient.DisconnectForClientFailure(
                    "world_actor_state_conflict",
                    error);
            }
        }

        private void OnSessionEnded(RealtimeClientError error)
        {
            State.Clear();
        }

        private void Unsubscribe()
        {
            if (!initialized || simulationClient == null)
            {
                return;
            }

            simulationClient.WorldActorSpawned -= OnSpawned;
            simulationClient.WorldActorStateReceived -= OnStateReceived;
            simulationClient.WorldActorDespawned -= OnDespawned;
            simulationClient.SessionEnded -= OnSessionEnded;
            initialized = false;
        }
    }
}
