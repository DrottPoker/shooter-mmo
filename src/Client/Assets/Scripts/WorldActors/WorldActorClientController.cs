using System;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    [DisallowMultipleComponent]
    public sealed class WorldActorClientController : MonoBehaviour
    {
        private IRealtimeWorldActorChannel worldActorChannel;
        private IRealtimeSession realtimeSession;
        private bool initialized;

        public WorldActorClientState State { get; } = new WorldActorClientState();

        public void Initialize(IRealtimeWorldActorChannel worldActors, IRealtimeSession session, TextCatalog texts)
        {
            if (worldActors == null)
            {
                throw new ArgumentNullException(nameof(worldActors));
            }

            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (texts == null)
            {
                throw new ArgumentNullException(nameof(texts));
            }

            if (initialized)
            {
                Unsubscribe();
            }

            worldActorChannel = worldActors;
            realtimeSession = session;
            worldActorChannel.WorldActorSpawned += OnSpawned;
            worldActorChannel.WorldActorStateReceived += OnStateReceived;
            worldActorChannel.WorldActorDespawned += OnDespawned;
            realtimeSession.SessionEnded += OnSessionEnded;
            initialized = true;

            State.UseTexts(texts);
            State.Clear();
            foreach (var spawn in worldActorChannel.SpawnedWorldActors)
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
                realtimeSession.DisconnectForClientFailure(
                    "world_actor_state_conflict",
                    error);
            }
        }

        private void OnStateReceived(RealtimeWorldActorState state)
        {
            if (!State.TryApplyState(state, out var error))
            {
                realtimeSession.DisconnectForClientFailure(
                    "world_actor_state_conflict",
                    error);
            }
        }

        private void OnDespawned(RealtimeWorldActorDespawn despawn)
        {
            if (!State.TryRemove(despawn, out var error))
            {
                realtimeSession.DisconnectForClientFailure(
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
            if (!initialized || worldActorChannel == null)
            {
                return;
            }

            worldActorChannel.WorldActorSpawned -= OnSpawned;
            worldActorChannel.WorldActorStateReceived -= OnStateReceived;
            worldActorChannel.WorldActorDespawned -= OnDespawned;
            realtimeSession.SessionEnded -= OnSessionEnded;
            initialized = false;
        }
    }
}
